using System.Text.Json;
using System.Text.Json.Serialization;
using SteamFinish.Core.Logging;

namespace SteamFinish.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, falling back to defaults on any error.</summary>
public sealed class SettingsStore(string path, ILog? log = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILog _log = log ?? NullLog.Instance;

    public string FilePath => path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            return (JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings()).Normalize();
        }
        catch (Exception e)
        {
            _log.Warn($"Could not read settings from '{path}': {e.Message}. Using defaults.");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(settings.Normalize(), Options);

            // Write beside the target first so a crash cannot leave a truncated settings file.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e)
        {
            _log.Error($"Could not save settings to '{path}'.", e);
        }
    }
}
