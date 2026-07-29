using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SteamFinish.Core.Startup;

/// <summary>Registers the app in the per-user Run key so it can start with Windows.</summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistrar
{
    public const string StartMinimizedArgument = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SteamFinish";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Adds or removes the Run entry. Returns false when the registry could not be written.</summary>
    public static bool SetEnabled(bool enabled, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var exe = executablePath ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{exe}\" {StartMinimizedArgument}", RegistryValueKind.String);
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
