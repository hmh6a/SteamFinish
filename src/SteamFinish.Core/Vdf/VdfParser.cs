using System.Text;

namespace SteamFinish.Core.Vdf;

/// <summary>
/// Minimal reader for Valve's text KeyValues format, which is what
/// <c>libraryfolders.vdf</c> and <c>appmanifest_*.acf</c> use.
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var index = 0;
        ParseInto(text, ref index, root);
        return root;
    }

    public static VdfNode ParseFile(string path)
    {
        // Steam writes these files as UTF-8; a BOM may or may not be present.
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    public static bool TryParseFile(string path, out VdfNode node)
    {
        try
        {
            node = ParseFile(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            node = new VdfNode();
            return false;
        }
    }

    private static void ParseInto(string text, ref int index, VdfNode parent)
    {
        while (true)
        {
            SkipTrivia(text, ref index);
            if (index >= text.Length)
            {
                return;
            }

            if (text[index] == '}')
            {
                index++;
                return;
            }

            var key = ReadToken(text, ref index);
            if (key.Length == 0)
            {
                index++; // Unexpected character: step over it rather than spinning.
                continue;
            }

            SkipTrivia(text, ref index);
            if (index >= text.Length)
            {
                parent.Set(key, new VdfNode(string.Empty));
                return;
            }

            if (text[index] == '{')
            {
                index++;
                var child = new VdfNode();
                ParseInto(text, ref index, child);
                parent.Set(key, child);
            }
            else
            {
                var value = ReadToken(text, ref index);
                parent.Set(key, new VdfNode(value));
                SkipConditional(text, ref index);
            }
        }
    }

    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
            }
            else if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] is not ('\n' or '\r'))
                {
                    index++;
                }
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Skips platform conditionals such as <c>[$WIN32]</c> that may trail a value.</summary>
    private static void SkipConditional(string text, ref int index)
    {
        var probe = index;
        SkipTrivia(text, ref probe);
        if (probe < text.Length && text[probe] == '[')
        {
            while (probe < text.Length && text[probe] != ']')
            {
                probe++;
            }

            index = Math.Min(probe + 1, text.Length);
        }
    }

    private static string ReadToken(string text, ref int index)
    {
        if (text[index] == '"')
        {
            index++;
            var builder = new StringBuilder();
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"')
                {
                    break;
                }

                if (c == '\\' && index < text.Length)
                {
                    var escaped = text[index++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        _ => escaped,
                    });
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        var start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}' or '"'))
        {
            index++;
        }

        return text[start..index];
    }
}
