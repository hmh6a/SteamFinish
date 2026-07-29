using System.Globalization;

namespace SteamFinish.Core.Vdf;

/// <summary>
/// A node in a parsed Valve Data Format document. A node is either a leaf holding a
/// string <see cref="Value"/> or an object holding <see cref="Children"/>.
/// </summary>
public sealed class VdfNode
{
    private static readonly IReadOnlyDictionary<string, VdfNode> NoChildren =
        new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, VdfNode>? _children;

    /// <summary>Creates an object node.</summary>
    public VdfNode() => _children = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a leaf node holding <paramref name="value"/>.</summary>
    public VdfNode(string value) => Value = value;

    /// <summary>The string value of a leaf node, or <c>null</c> for object nodes.</summary>
    public string? Value { get; }

    public bool IsObject => _children is not null;

    public IReadOnlyDictionary<string, VdfNode> Children => _children ?? NoChildren;

    /// <summary>Case-insensitive child lookup; <c>null</c> when the key is absent.</summary>
    public VdfNode? this[string key] =>
        _children is not null && _children.TryGetValue(key, out var node) ? node : null;

    public string? GetString(string key) => this[key]?.Value;

    public long GetInt64(string key, long fallback = 0) =>
        long.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>
    /// Steam nests the payload of most files one level deep (for example <c>"AppState" { ... }</c>).
    /// Returns that single object child, falling back to this node when the shape differs.
    /// </summary>
    public VdfNode Unwrap(string? expectedKey = null)
    {
        if (expectedKey is not null && this[expectedKey] is { IsObject: true } named)
        {
            return named;
        }

        foreach (var child in Children.Values)
        {
            if (child.IsObject)
            {
                return child;
            }
        }

        return this;
    }

    internal void Set(string key, VdfNode value)
    {
        // Later definitions win, which matches Steam's own behaviour for duplicated keys.
        _children![key] = value;
    }
}
