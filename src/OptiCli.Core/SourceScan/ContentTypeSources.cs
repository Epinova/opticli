using System.Text.RegularExpressions;

namespace OptiCli.Core.SourceScan;

public sealed record ClassMatch(ClassDeclaration Class, string MatchedBy);

public sealed record ViewMatch(string File, string MatchedBy);

/// <summary>What the C# declaration of a content type property says about it.</summary>
/// <param name="Tab">Resolved <c>[Display(GroupName = ...)]</c>; the raw expression when it can't be resolved.</param>
/// <param name="CultureSpecific">Null when the property has no <c>[CultureSpecific]</c> attribute.</param>
/// <param name="AllowedTypes">Null when the property has no <c>[AllowedTypes]</c> attribute.</param>
/// <param name="UiHint">The <c>[UIHint]</c> argument as written (a literal or a constant's name), if any.</param>
public sealed record PropertySource(
    string File,
    int Line,
    string DeclaredIn,
    int? Order,
    string? Tab,
    string? DisplayName,
    bool Required,
    bool? CultureSpecific,
    AllowedTypesDeclaration? AllowedTypes = null,
    string? UiHint = null);

/// <summary>
/// Finds the C# class behind a content type, the declarations of its properties (through the base
/// class chain) and the Razor views that render it.
/// </summary>
public static partial class ContentTypeSources
{
    /// <summary>EPiServer.DataAbstraction.SystemTabNames, which lives in the CMS assembly rather than the site's code.</summary>
    private static readonly Dictionary<string, string> SystemTabs = new(StringComparer.Ordinal)
    {
        ["SystemTabNames.Content"] = "Information",
        ["SystemTabNames.Settings"] = "Advanced",
        ["SystemTabNames.PageHeader"] = "EPiServerCMS_SettingsPanel",
    };

    private const int MaxBaseDepth = 16;

    /// <summary>
    /// Classes whose ContentType attribute carries <paramref name="guid"/>; failing that, classes named
    /// <paramref name="className"/>, narrowed to <paramref name="ns"/> when that disambiguates.
    /// </summary>
    public static IReadOnlyList<ClassMatch> FindClasses(CSharpSourceIndex index, Guid guid, string className, string? ns)
    {
        var byGuid = index.Classes.Where(c => c.ContentTypeGuid == guid).ToList();
        if (byGuid.Count > 0)
        {
            return byGuid.Select(c => new ClassMatch(c, "guid")).ToList();
        }

        var byName = index.Classes.Where(c => c.Name == className).ToList();
        var inNamespace = byName.Where(c => c.Namespace == ns).ToList();
        return (inNamespace.Count > 0 ? inNamespace : byName).Select(c => new ClassMatch(c, "className")).ToList();
    }

    /// <summary>
    /// Property declarations by name for <paramref name="type"/> and its base classes found in the index,
    /// including every part of partial classes. The most derived declaration wins; an override without
    /// attributes takes them from the declaration it overrides.
    /// </summary>
    public static IReadOnlyDictionary<string, PropertySource> FindProperties(CSharpSourceIndex index, ClassDeclaration type)
    {
        var result = new Dictionary<string, PropertySource>(StringComparer.Ordinal);
        var visited = new HashSet<(string?, string)>();
        IReadOnlyList<ClassDeclaration>? parts = PartsOf(index, type);

        for (var depth = 0; parts is not null && depth < MaxBaseDepth && visited.Add((parts[0].Namespace, parts[0].Name)); depth++)
        {
            foreach (var (name, declaration) in parts.SelectMany(part => PropertiesOf(index, part)))
            {
                result[name] = result.TryGetValue(name, out var derived) ? Inherit(derived, declaration) : declaration;
            }
            parts = BaseClass(index, parts);
        }
        return result;
    }

    /// <summary>
    /// Views named after the type (<c>X.cshtml</c>, <c>_X.cshtml</c>, <c>Components/X/Default.cshtml</c>)
    /// and views whose <c>@model</c> mentions it.
    /// </summary>
    public static IReadOnlyList<ViewMatch> FindViews(string root, IReadOnlyCollection<string> typeNames)
    {
        var result = new List<ViewMatch>();
        foreach (var file in SourceTree.EnumerateFiles(root, "*.cshtml"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var parent = Path.GetFileName(Path.GetDirectoryName(file));
            var grandparent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file)));

            string? matchedBy = null;
            if (typeNames.Contains(fileName))
            {
                matchedBy = "fileName";
            }
            else if (fileName.StartsWith('_') && typeNames.Contains(fileName[1..]))
            {
                matchedBy = "partialName";
            }
            else if (fileName == "Default" && grandparent == "Components" && parent is not null && typeNames.Contains(parent))
            {
                matchedBy = "viewComponent";
            }
            else if (ModelMentions(file, typeNames))
            {
                matchedBy = "model";
            }

            if (matchedBy is not null)
            {
                result.Add(new ViewMatch(file, matchedBy));
            }
        }
        return result;
    }

    private static bool ModelMentions(string file, IReadOnlyCollection<string> typeNames)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var model = ModelPattern().Match(text);
        if (!model.Success)
        {
            return false;
        }
        return IdentifierPattern().Matches(model.Groups["type"].Value).Any(m => typeNames.Contains(m.Value));
    }

    private static PropertySource Inherit(PropertySource derived, PropertySource overridden) => derived with
    {
        Order = derived.Order ?? overridden.Order,
        Tab = derived.Tab ?? overridden.Tab,
        DisplayName = derived.DisplayName ?? overridden.DisplayName,
        Required = derived.Required || overridden.Required,
        CultureSpecific = derived.CultureSpecific ?? overridden.CultureSpecific,
        AllowedTypes = derived.AllowedTypes ?? overridden.AllowedTypes,
        UiHint = derived.UiHint ?? overridden.UiHint,
    };

    /// <summary>All declarations of the same class: a partial class is spread over several files.</summary>
    private static IReadOnlyList<ClassDeclaration> PartsOf(CSharpSourceIndex index, ClassDeclaration type) =>
        index.Classes.Where(c => c.Name == type.Name && c.Namespace == type.Namespace).ToList();

    private static IReadOnlyList<ClassDeclaration>? BaseClass(CSharpSourceIndex index, IReadOnlyList<ClassDeclaration> parts)
    {
        var self = parts[0];
        foreach (var name in parts.SelectMany(p => p.BaseTypes).Distinct())
        {
            var matches = index.Classes.Where(c => !c.IsInterface && c.Name == name && !(c.Name == self.Name && c.Namespace == self.Namespace)).ToList();
            if (matches.Count > 0)
            {
                return PartsOf(index, matches.FirstOrDefault(c => c.Namespace == self.Namespace) ?? matches[0]);
            }
        }
        return null;
    }

    private static IEnumerable<KeyValuePair<string, PropertySource>> PropertiesOf(CSharpSourceIndex index, ClassDeclaration type)
    {
        var text = index.Text(type.File);
        var siblings = index.Classes.Where(c => c.File == type.File).ToList();
        var body = text[(type.BodyStart + 1)..type.BodyEnd];

        foreach (Match property in PropertyPattern().Matches(body))
        {
            var position = type.BodyStart + 1 + property.Index;
            // Skip properties that belong to a class nested inside this one.
            if (siblings.Where(c => c.Contains(position)).MaxBy(c => c.BodyStart) != type)
            {
                continue;
            }

            var memberStart = MemberStart(body, property.Index);
            var attributes = body[memberStart..property.Index];
            if (IgnorePattern().IsMatch(attributes))
            {
                continue;
            }

            var display = DisplayArguments(attributes);
            var name = property.Groups["name"].Value;
            yield return new(name, new PropertySource(
                type.File,
                CSharpText.LineOf(text, position + property.Groups["name"].Index - property.Index),
                type.Name,
                display is not null && OrderPattern().Match(display) is { Success: true } order ? int.Parse(order.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                display is not null && GroupNamePattern().Match(display) is { Success: true } group ? ResolveTab(index, group) : null,
                display is not null && NamePattern().Match(display) is { Success: true } displayName ? displayName.Groups["value"].Value : null,
                RequiredPattern().IsMatch(attributes),
                CultureSpecificPattern().Match(attributes) is { Success: true } culture ? culture.Groups["value"].Value != "false" : null,
                AllowedTypesParser.Parse(attributes),
                UiHintPattern().Match(attributes) is { Success: true } hint ? hint.Groups["value"].Value.Trim().Trim('"') : null));
        }
    }

    /// <summary>
    /// Where the declaration ending at <paramref name="end"/> starts, attributes included: just after the previous
    /// <c>;</c>, <c>{</c> or <c>}</c> outside attribute brackets, so array initialisers inside an attribute
    /// (<c>[AllowedTypes(AllowedTypes = new[] { typeof(X) })]</c>) don't cut it short.
    /// </summary>
    private static int MemberStart(string body, int end)
    {
        var depth = 0;
        for (var i = end - 1; i >= 0; i--)
        {
            switch (body[i])
            {
                case ']' or ')':
                    depth++;
                    break;
                case '[' or '(':
                    depth--;
                    break;
                case ';' or '{' or '}' when depth <= 0:
                    return i + 1;
            }
        }
        return 0;
    }

    private static string? DisplayArguments(string attributes)
    {
        var start = DisplayPattern().Match(attributes);
        if (!start.Success)
        {
            return null;
        }
        var open = start.Index + start.Length - 1;
        var close = CSharpText.FindClosing(attributes, open, '(', ')');
        return attributes[(open + 1)..Math.Min(close, attributes.Length)];
    }

    private static string ResolveTab(CSharpSourceIndex index, Match group)
    {
        if (group.Groups["literal"].Success)
        {
            return group.Groups["literal"].Value;
        }

        var expression = group.Groups["expression"].Value;
        var parts = expression.Split('.');
        var shortName = parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : expression;
        return SystemTabs.GetValueOrDefault(shortName) ?? index.Constants.GetValueOrDefault(shortName) ?? expression;
    }

    [GeneratedRegex(@"^[ \t]*public\s+(?:(?:virtual|override|new|sealed|required|abstract)\s+)*(?!(?:class|record|struct|interface|enum|event|delegate|static|const|readonly)\b)(?<type>[\w.<>\[\],?]+(?:\s*<[^>{};]*>)?\??)\s+(?<name>[A-Za-z_]\w*)\s*(?:\{|=>)", RegexOptions.Multiline)]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(@"\bDisplay(?:Attribute)?\s*\(")]
    private static partial Regex DisplayPattern();

    [GeneratedRegex(@"\bOrder\s*=\s*(?<value>-?\d+)")]
    private static partial Regex OrderPattern();

    [GeneratedRegex(@"\bGroupName\s*=\s*(?:""(?<literal>[^""]*)""|(?<expression>[\w.]+))")]
    private static partial Regex GroupNamePattern();

    [GeneratedRegex(@"\bName\s*=\s*""(?<value>[^""]*)""")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"[\[,]\s*(?:[\w.]*\.)?Required(?:Attribute)?\s*[\](,]")]
    private static partial Regex RequiredPattern();

    [GeneratedRegex(@"[\[,]\s*(?:[\w.]*\.)?CultureSpecific(?:Attribute)?\s*(?:\(\s*(?<value>true|false)\s*\))?\s*[\],]")]
    private static partial Regex CultureSpecificPattern();

    [GeneratedRegex(@"[\[,]\s*(?:[\w.]*\.)?Ignore(?:Attribute)?\s*[\](,]")]
    private static partial Regex IgnorePattern();

    [GeneratedRegex(@"[\[,]\s*(?:[\w.]*\.)?UIHint(?:Attribute)?\s*\(\s*(?<value>""[^""]*""|[\w.]+)")]
    private static partial Regex UiHintPattern();

    [GeneratedRegex(@"^\s*@model\s+(?<type>[^\r\n]+)", RegexOptions.Multiline)]
    private static partial Regex ModelPattern();

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex IdentifierPattern();
}
