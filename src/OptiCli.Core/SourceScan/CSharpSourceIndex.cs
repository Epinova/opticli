using System.Text.RegularExpressions;

namespace OptiCli.Core.SourceScan;

/// <summary>
/// Class declarations and string constants across a source tree, found with regexes. Built once per
/// command; a site with ~1000 C# files indexes in well under a second.
/// </summary>
public sealed partial class CSharpSourceIndex
{
    private readonly Dictionary<string, string> _texts;

    private CSharpSourceIndex(string root, Dictionary<string, string> texts, List<ClassDeclaration> classes, Dictionary<string, string> constants)
    {
        Root = root;
        _texts = texts;
        Classes = classes;
        Constants = constants;
    }

    public string Root { get; }

    public IReadOnlyList<ClassDeclaration> Classes { get; }

    /// <summary><c>ClassName.ConstName</c> → value, for resolving <c>GroupName = TabNames.Content</c>.</summary>
    public IReadOnlyDictionary<string, string> Constants { get; }

    public string Text(string file) => _texts[file];

    public static CSharpSourceIndex Build(string root)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var classes = new List<ClassDeclaration>();
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in SourceTree.EnumerateFiles(root, "*.cs"))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            texts[file] = text;
            var fileClasses = ParseClasses(file, text);
            classes.AddRange(fileClasses);

            foreach (Match constant in ConstantPattern().Matches(text))
            {
                var owner = fileClasses.Where(c => c.Contains(constant.Index)).MaxBy(c => c.BodyStart);
                if (owner is not null)
                {
                    var name = constant.Groups["name"].Value;
                    constants.TryAdd($"{owner.Name}.{name}", constant.Groups["value"].Success ? constant.Groups["value"].Value : constant.Groups["nameof"].Value);
                }
            }
        }

        return new CSharpSourceIndex(root, texts, classes, constants);
    }

    private static List<ClassDeclaration> ParseClasses(string file, string text)
    {
        var ns = NamespacePattern().Match(text) is { Success: true } n ? n.Groups["ns"].Value : null;
        var result = new List<ClassDeclaration>();
        var previousEnd = 0;

        foreach (Match match in ClassPattern().Matches(text))
        {
            var open = text.IndexOf('{', match.Index + match.Length);
            var semicolon = text.IndexOf(';', match.Index + match.Length);
            if (open < 0 || (semicolon >= 0 && semicolon < open))
            {
                // `record Foo(...);` or a declaration we can't follow: no body to scan.
                continue;
            }

            var header = text[(match.Index + match.Length)..open];
            var guid = ContentTypeGuid(text[previousEnd..match.Index]);
            var close = CSharpText.FindClosing(text, open);

            result.Add(new ClassDeclaration(
                match.Groups["name"].Value,
                file,
                CSharpText.LineOf(text, match.Index),
                ns,
                BaseTypes(header),
                guid,
                open,
                close));
            previousEnd = match.Index + match.Length;
        }
        return result;
    }

    /// <summary>
    /// The GUID of the last ContentType-style attribute before the class keyword, provided nothing but
    /// attributes separates them (attributes never contain ';', members always do).
    /// </summary>
    private static Guid? ContentTypeGuid(string preamble)
    {
        var last = ContentTypeGuidPattern().Matches(preamble).LastOrDefault();
        if (last is null || preamble.IndexOf(';', last.Index + last.Length) >= 0)
        {
            return null;
        }
        return Guid.TryParse(last.Groups["guid"].Value, out var guid) ? guid : null;
    }

    /// <summary>The types after ':' in a class header, skipping a primary constructor and generic constraints.</summary>
    private static IReadOnlyList<string> BaseTypes(string header)
    {
        var depth = 0;
        for (var i = 0; i < header.Length; i++)
        {
            switch (header[i])
            {
                case '<' or '(':
                    depth++;
                    break;
                case '>' or ')':
                    depth--;
                    break;
                case ':' when depth == 0:
                    var list = header[(i + 1)..];
                    var where = WherePattern().Match(list);
                    if (where.Success)
                    {
                        list = list[..where.Index];
                    }
                    return CSharpText.SplitTopLevel(list).Select(CSharpText.SimpleTypeName).Where(n => n.Length > 0).ToList();
            }
        }
        return [];
    }

    [GeneratedRegex(@"^[ \t]*(?:(?:public|internal|private|protected|sealed|abstract|static|partial|file|unsafe|new)\s+)*(?:class|record(?:\s+class)?)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Multiline)]
    private static partial Regex ClassPattern();

    [GeneratedRegex(@"^\s*namespace\s+(?<ns>[\w.]+)", RegexOptions.Multiline)]
    private static partial Regex NamespacePattern();

    [GeneratedRegex(@"\b\w*ContentType(?:Attribute)?\s*\([^;]*?\bGUID\s*=\s*""(?<guid>[0-9A-Fa-f{}-]{32,38})""")]
    private static partial Regex ContentTypeGuidPattern();

    /// <summary><c>const string X = "value";</c> or <c>const string X = nameof(X);</c></summary>
    [GeneratedRegex(@"\bconst\s+string\s+(?<name>\w+)\s*=\s*(?:""(?<value>[^""\\]*)""|nameof\(\s*(?:[\w.]*\.)?(?<nameof>\w+)\s*\))\s*;")]
    private static partial Regex ConstantPattern();

    [GeneratedRegex(@"\bwhere\b")]
    private static partial Regex WherePattern();
}
