namespace OptiCli.Core.SourceScan;

/// <summary>
/// Just enough C# lexing for regex-based scanning: bracket matching that skips strings, chars and
/// comments. Good enough for content type classes; not a parser.
/// </summary>
internal static class CSharpText
{
    /// <summary>Index of the bracket closing the one at <paramref name="openIndex"/>, or the text end.</summary>
    public static int FindClosing(string text, int openIndex, char open = '{', char close = '}')
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i = text.IndexOf('\n', i) is var eol && eol < 0 ? text.Length : eol;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end && end < 0 ? text.Length : end + 1;
            }
            else if (c == '"')
            {
                i = SkipString(text, i);
            }
            else if (c == '\'')
            {
                i = SkipChar(text, i);
            }
            else if (c == open)
            {
                depth++;
            }
            else if (c == close && --depth == 0)
            {
                return i;
            }
        }
        return text.Length;
    }

    public static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    /// <summary>Strips generic arguments and a namespace/alias qualifier: <c>Base.PageBase&lt;T&gt;</c> → <c>PageBase</c>.</summary>
    public static string SimpleTypeName(string type)
    {
        var name = type.Trim();
        var cut = name.IndexOfAny(['<', '(']);
        if (cut >= 0)
        {
            name = name[..cut];
        }
        var dot = Math.Max(name.LastIndexOf('.'), name.LastIndexOf(':'));
        return name[(dot + 1)..].Trim();
    }

    /// <summary>Splits on commas that are not inside &lt;&gt;, (), [] or {}.</summary>
    public static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<' or '(' or '[' or '{':
                    depth++;
                    break;
                case '>' or ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return text[start..];
    }

    private static int SkipString(string text, int quote)
    {
        if (string.CompareOrdinal(text, quote, "\"\"\"", 0, 3) == 0)
        {
            var end = text.IndexOf("\"\"\"", quote + 3, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + 2;
        }

        var verbatim = quote > 0 && (text[quote - 1] == '@' || (quote > 1 && text[quote - 2] == '@'));
        for (var i = quote + 1; i < text.Length; i++)
        {
            if (verbatim)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i++;
                        continue;
                    }
                    return i;
                }
            }
            else if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '"' || text[i] == '\n')
            {
                return i;
            }
        }
        return text.Length;
    }

    private static int SkipChar(string text, int quote)
    {
        var end = text.IndexOf('\'', quote + (quote + 1 < text.Length && text[quote + 1] == '\\' ? 3 : 2));
        return end < 0 || end - quote > 8 ? quote : end;
    }
}
