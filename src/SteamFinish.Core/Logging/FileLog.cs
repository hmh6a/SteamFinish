using System.Globalization;
using System.Text;

namespace SteamFinish.Core.Logging;

/// <summary>
/// Appends to a single rolling text file under the application data folder. Writing is guarded by a
/// lock and failures are swallowed: logging must never take the app down.
/// </summary>
public sealed class FileLog : ILog
{
    private const long MaxBytes = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly string _path;

    public FileLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>When false, calls are dropped without touching the disk.</summary>
    public bool Enabled { get; set; } = true;

    public string FilePath => _path;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        if (!Enabled)
        {
            return;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}{Environment.NewLine}");

        lock (_gate)
        {
            try
            {
                Roll();
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nothing sensible to do if the log itself cannot be written.
            }
        }
    }

    private void Roll()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        var previous = _path + ".1";
        File.Delete(previous);
        File.Move(_path, previous);
    }
}
