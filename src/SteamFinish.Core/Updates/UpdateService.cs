using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SteamFinish.Core.Logging;

namespace SteamFinish.Core.Updates;

/// <summary>
/// Checks GitHub for a newer release and, on request, installs it.
///
/// A running program cannot overwrite its own executable, so the download is staged to a temporary
/// folder and a small script does the swap: it waits for this process to exit, replaces the files
/// and starts the new build.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateService(string repository, ILog? log = null) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly ILog _log = log ?? NullLog.Instance;

    /// <summary>
    /// The version of the running build. Taken from the entry assembly — the app carries the release
    /// version, while this library is left at its default.
    /// </summary>
    public static string CurrentVersion { get; } = ReleaseReader.Normalize(
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly)
            .GetName().Version?.ToString(3) ?? "0.0.0");

    public string Repository => repository;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository) || !repository.Contains('/', StringComparison.Ordinal))
        {
            return UpdateCheckResult.Failed("No GitHub repository is configured to check.");
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("SteamFinish");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 also means "private repository" when no token is being sent.
                return UpdateCheckResult.Failed($"GitHub answered {(int)response.StatusCode}.");
            }

            if (ReleaseReader.Read(body) is not { } release)
            {
                return UpdateCheckResult.Failed("The latest release has no download attached.");
            }

            return ReleaseReader.IsNewer(release.Version, CurrentVersion)
                ? new UpdateCheckResult(true, release, null)
                : UpdateCheckResult.UpToDate();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return UpdateCheckResult.Failed($"Could not reach GitHub: {e.Message}");
        }
    }

    /// <summary>
    /// Downloads and verifies the release, then hands over to a script that swaps the files once this
    /// process exits. Returns only if the handover could not be started.
    /// </summary>
    public async Task<string?> InstallAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installFolder = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(installFolder))
        {
            return "The install folder could not be determined.";
        }

        var staging = Path.Combine(Path.GetTempPath(), "SteamFinishUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            var zipPath = Path.Combine(staging, "update.zip");
            await DownloadAsync(update, zipPath, progress, cancellationToken).ConfigureAwait(false);

            if (update.ChecksumUrl is { Length: > 0 })
            {
                var verified = await VerifyAsync(zipPath, update.ChecksumUrl, cancellationToken).ConfigureAwait(false);
                if (verified is { } problem)
                {
                    return problem;
                }
            }

            var unpacked = Path.Combine(staging, "unpacked");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, unpacked);

            if (!Directory.EnumerateFiles(unpacked, "SteamFinish.exe", SearchOption.AllDirectories).Any())
            {
                return "The download does not contain SteamFinish.exe.";
            }

            var script = WriteApplyScript(staging, unpacked, installFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -ProcessId {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            _log.Info($"Update to {update.Version} staged; handing over to the apply script.");
            return null;
        }
        catch (Exception e)
        {
            _log.Error("The update could not be installed.", e);
            TryDelete(staging);
            return e.Message;
        }
    }

    private async Task DownloadAsync(
        UpdateInfo update,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("SteamFinish");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? update.SizeBytes;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;

            if (total > 0)
            {
                progress?.Report((double)written / total);
            }
        }
    }

    /// <summary>Returns a message when the download does not match its published hash, else null.</summary>
    private async Task<string?> VerifyAsync(string zipPath, string checksumUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, checksumUrl);
            request.Headers.UserAgent.ParseAdd("SteamFinish");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var expected = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(expected))
            {
                return null;
            }

            await using var stream = File.OpenRead(zipPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

            return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase)
                ? null
                : "The download did not match its published checksum and was discarded.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // An unreachable checksum is not proof of a bad download.
            _log.Warn($"The checksum could not be fetched: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes the hand-over script. It waits for this process to exit before touching anything, then
    /// copies the new build over the install folder and starts it.
    /// </summary>
    private static string WriteApplyScript(string staging, string unpacked, string installFolder)
    {
        var script = Path.Combine(staging, "apply.ps1");
        var body = $$"""
            param([int] $ProcessId)

            $ErrorActionPreference = 'Stop'
            $unpacked = '{{unpacked.Replace("'", "''")}}'
            $install  = '{{installFolder.Replace("'", "''")}}'
            $staging  = '{{staging.Replace("'", "''")}}'

            # Wait for SteamFinish to close; a running exe cannot be replaced.
            try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch { }
            Start-Sleep -Milliseconds 700

            try {
                Copy-Item -Path (Join-Path $unpacked '*') -Destination $install -Recurse -Force
                Start-Process -FilePath (Join-Path $install 'SteamFinish.exe')
            }
            finally {
                Start-Sleep -Seconds 2
                Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;

        File.WriteAllText(script, body, new UTF8Encoding(true));
        return script;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is harmless.
        }
    }

    public void Dispose() => _http.Dispose();
}
