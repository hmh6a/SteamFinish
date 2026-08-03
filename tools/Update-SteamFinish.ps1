<#
.SYNOPSIS
    Replaces the copy of SteamFinish on your Desktop with the latest GitHub release.

.DESCRIPTION
    Downloads the newest release asset, verifies its SHA256 when the release publishes one, closes a
    running SteamFinish, and swaps the folder on the Desktop.

    Settings, logs and your Telegram configuration live in %AppData%\SteamFinish and are untouched.

    Only a folder that actually contains SteamFinish.exe is ever deleted; anything else on the
    Desktop is left alone.

.PARAMETER Repo
    The GitHub repository as owner/name. Remembered after the first run.

.PARAMETER Destination
    Where to install. Defaults to <Desktop>\SteamFinish.

.PARAMETER NoLaunch
    Do not start SteamFinish after updating.

.EXAMPLE
    .\Update-SteamFinish.ps1 -Repo hmh6a/SteamFinish
#>
[CmdletBinding()]
param(
    [string] $Repo,
    [string] $Destination,
    [switch] $NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$configPath = Join-Path $env:APPDATA 'SteamFinish\update.json'

function Resolve-Repo {
    param([string] $Explicit)

    if ($Explicit) { return $Explicit }

    if (Test-Path -LiteralPath $configPath) {
        try {
            $saved = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).Repo
            if ($saved) { return $saved }
        }
        catch { }
    }

    # Fall back to the checkout this script came from, if git is available.
    $git = (Get-Command git -ErrorAction SilentlyContinue)
    if ($git -and $PSScriptRoot) {
        try {
            $url = & $git.Source -C (Split-Path -Parent $PSScriptRoot) remote get-url origin 2>$null
            if ($url -match 'github\.com[:/](?<owner>[^/]+)/(?<name>[^/.]+)') {
                return "$($Matches.owner)/$($Matches.name)"
            }
        }
        catch { }
    }

    throw "Tell me which repository to update from, once: .\Update-SteamFinish.ps1 -Repo owner/name"
}

function Save-Repo {
    param([string] $Value)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $configPath) | Out-Null
    @{ Repo = $Value } | ConvertTo-Json | Out-File -FilePath $configPath -Encoding utf8
}

<#
.SYNOPSIS
    Swaps the installed folder for a freshly unpacked one.
.DESCRIPTION
    The only destructive step in this script, kept in one place so it can be tested on its own.
    A folder is removed only when it holds SteamFinish.exe, so pointing -Destination at the Desktop
    itself, or at any folder that is not one of our installs, refuses rather than deleting it.
#>
function Install-Payload {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        if (-not (Test-Path -LiteralPath (Join-Path $Destination 'SteamFinish.exe'))) {
            throw "'$Destination' exists but holds no SteamFinish.exe, so it was left alone. Point -Destination somewhere else."
        }

        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
}

# Dot-sourcing loads the functions without updating anything, which is how the guard is tested.
if ($MyInvocation.InvocationName -eq '.') { return }

$Repo = Resolve-Repo -Explicit $Repo
if (-not $Destination) {
    $Destination = Join-Path ([Environment]::GetFolderPath('Desktop')) 'SteamFinish'
}

Write-Host "Repository : $Repo"
Write-Host "Installing : $Destination"

# ---------------------------------------------------------------- find the release
$headers = @{ 'User-Agent' = 'SteamFinish-Updater'; 'Accept' = 'application/vnd.github+json' }
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }

try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers -TimeoutSec 60
}
catch {
    throw "Could not read the latest release of '$Repo': $($_.Exception.Message)"
}

$asset = $release.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1
if (-not $asset) { throw "Release $($release.tag_name) has no .zip to download." }

Write-Host "Latest     : $($release.tag_name) ($($asset.name))"

# Skip the work when the installed build is already this version.
$installedExe = Join-Path $Destination 'SteamFinish.exe'
if (Test-Path -LiteralPath $installedExe) {
    $installed = (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion
    $wanted = ($release.tag_name -replace '^v', '')
    if ($installed -and $installed.StartsWith($wanted)) {
        Write-Host "Already on $installed. Nothing to do." -ForegroundColor Green
        Save-Repo $Repo
        return
    }

    Write-Host "Installed  : $installed"
}

# ---------------------------------------------------------------- download
$staging = Join-Path ([IO.Path]::GetTempPath()) ("SteamFinishUpdate_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    $zipPath = Join-Path $staging $asset.name
    Write-Host "Downloading $([math]::Round($asset.size / 1MB, 1)) MB..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -Headers $headers -TimeoutSec 900

    # Verify against the checksum published beside the zip, when there is one.
    $checksum = $release.assets | Where-Object { $_.name -eq "$($asset.name).sha256" } | Select-Object -First 1
    if ($checksum) {
        $expected = ((Invoke-WebRequest -Uri $checksum.browser_download_url -Headers $headers -TimeoutSec 60).Content -split '\s+')[0]
        $actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash
        if ($actual -ne $expected.Trim()) {
            throw "The download does not match its published checksum. Expected $expected, got $actual."
        }

        Write-Host "Checksum   : verified" -ForegroundColor Green
    }
    else {
        Write-Warning "This release publishes no checksum; the download could not be verified."
    }

    $extracted = Join-Path $staging 'unpacked'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extracted -Force

    $newExe = Get-ChildItem -LiteralPath $extracted -Filter 'SteamFinish.exe' -Recurse -File | Select-Object -First 1
    if (-not $newExe) { throw "The downloaded zip does not contain SteamFinish.exe." }

    # ---------------------------------------------------------------- close the running copy
    $running = Get-Process SteamFinish -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Closing the running SteamFinish..."
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }

    # ---------------------------------------------------------------- replace
    Install-Payload -Source $extracted -Destination $Destination

    $version = (Get-Item -LiteralPath (Join-Path $Destination 'SteamFinish.exe')).VersionInfo.FileVersion
    Write-Host ""
    Write-Host "Updated to $($release.tag_name) ($version)" -ForegroundColor Green
    Write-Host "Your settings in %AppData%\SteamFinish were kept."

    Save-Repo $Repo

    if (-not $NoLaunch) {
        Start-Process -FilePath (Join-Path $Destination 'SteamFinish.exe')
        Write-Host "Started."
    }
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
