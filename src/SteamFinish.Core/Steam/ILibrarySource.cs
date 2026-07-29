using System.Runtime.Versioning;

namespace SteamFinish.Core.Steam;

/// <summary>Supplies the library roots the scanner should look at.</summary>
public interface ILibrarySource
{
    IReadOnlyList<string> GetLibraryRoots();
}

/// <summary>A fixed set of roots; used for manual configuration and in tests.</summary>
public sealed class FixedLibrarySource(IEnumerable<string> roots) : ILibrarySource
{
    private readonly string[] _roots = roots.ToArray();

    public IReadOnlyList<string> GetLibraryRoots() => _roots;
}

/// <summary>
/// Detects libraries through the registry and <c>libraryfolders.vdf</c>, optionally merged with
/// manually configured roots. Detection is cached briefly because it touches the registry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoLibrarySource(Func<bool> autoDetect, Func<IReadOnlyList<string>> manualRoots) : ILibrarySource
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(20);

    private readonly Lock _gate = new();
    private IReadOnlyList<string> _cached = [];

    /// <summary>Null means "never detected"; subtracting a sentinel tick value would overflow.</summary>
    private long? _cachedAtTicks;

    public IReadOnlyList<string> GetLibraryRoots()
    {
        var manual = manualRoots()
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimEnd('\\'))
            .Where(p => Directory.Exists(Path.Combine(p, "steamapps")))
            .ToList();

        if (!autoDetect())
        {
            return Deduplicate(manual);
        }

        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_cachedAtTicks is not { } cachedAt || now - cachedAt > CacheLifetime.TotalMilliseconds)
            {
                _cached = SteamLocator.FindLibraries();
                _cachedAtTicks = now;
            }

            return Deduplicate([.. _cached, .. manual]);
        }
    }

    /// <summary>Drops the detection cache so the next scan re-reads the registry.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cachedAtTicks = null;
        }
    }

    private static IReadOnlyList<string> Deduplicate(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return paths.Where(p => seen.Add(p)).ToArray();
    }
}
