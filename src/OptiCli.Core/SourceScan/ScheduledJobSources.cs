using System.Globalization;
using System.Text.RegularExpressions;
using OptiCli.Protocol;

namespace OptiCli.Core.SourceScan;

/// <summary>A class with <c>[ScheduledPlugIn]</c> in the site's source: a scheduled job the site registers when it starts.</summary>
/// <param name="TypeName">The class's full name, as the CMS writes it to <c>tblScheduledItem.TypeName</c>.</param>
/// <param name="Guid">The attribute's <c>GUID</c>, which becomes the job's id; null without one.</param>
/// <param name="IntervalType">The attribute's <c>IntervalType</c> as a <c>ScheduledIntervalType</c> value; 0 without one.</param>
/// <param name="File">The class's file, relative to the source root.</param>
public sealed record ScheduledJobSource(
    string TypeName,
    Guid? Guid,
    string? DisplayName,
    string? Description,
    int IntervalType,
    int IntervalLength,
    bool Restartable,
    string File,
    int Line);

/// <summary>
/// Finds <c>[ScheduledPlugIn(...)]</c> classes in a source index, with what the attribute says. The CMS registers each at
/// startup: it looks the row up by the attribute's GUID first, then by class and assembly (finding 0.4 of the plan), so
/// rows are matched the same way.
/// </summary>
public static partial class ScheduledJobSources
{
    public static IReadOnlyList<ScheduledJobSource> Find(CSharpSourceIndex index)
    {
        var result = new List<ScheduledJobSource>();
        foreach (var file in index.Classes.Select(c => c.File).Distinct())
        {
            var text = index.Text(file);
            foreach (Match attribute in AttributePattern().Matches(text))
            {
                var open = attribute.Index + attribute.Length - 1;
                var close = CSharpText.FindClosing(text, open, '(', ')');
                if (close >= text.Length)
                {
                    continue;
                }
                // The class the attribute is on: the first one after it, with nothing but attributes and modifiers between.
                var type = index.Classes
                    .Where(c => c.File == file && c.BodyStart > close)
                    .OrderBy(c => c.BodyStart)
                    .FirstOrDefault();
                if (type is null || text[close..type.BodyStart].IndexOfAny([';', '{', '}']) >= 0)
                {
                    continue;
                }
                var arguments = text[(open + 1)..close];
                result.Add(new ScheduledJobSource(
                    type.Namespace is { } ns ? $"{ns}.{type.Name}" : type.Name,
                    Named(arguments, "GUID") is { } guid && System.Guid.TryParse(guid, out var parsed) ? parsed : null,
                    Text(index, Named(arguments, "DisplayName") ?? NamedExpression(arguments, "DisplayName")),
                    Text(index, Named(arguments, "Description") ?? NamedExpression(arguments, "Description")),
                    IntervalPattern().Match(arguments) is { Success: true } interval ? IntervalTypeOf(interval.Groups["type"].Value) : 0,
                    LengthPattern().Match(arguments) is { Success: true } length ? int.Parse(length.Groups["value"].Value, CultureInfo.InvariantCulture) : 0,
                    RestartablePattern().IsMatch(arguments),
                    Path.GetRelativePath(index.Root, file),
                    type.Line));
            }
        }
        return result;
    }

    /// <summary>
    /// The assemblies the projects in the source tree build (their <c>AssemblyName</c>, else the file name): a job row in
    /// one of them whose class the scan doesn't find is a job whose class was removed, which the CMS leaves behind.
    /// </summary>
    public static IReadOnlySet<string> Assemblies(string root) =>
        SourceTree.EnumerateFiles(root, "*.csproj")
            .Select(path => Discovery.CsprojFile.TryLoad(path)?.AssemblyName ?? Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The source of the job with this id and class: by the attribute's GUID (the job's id), else by class name.</summary>
    public static ScheduledJobSource? Match(IReadOnlyList<ScheduledJobSource> sources, Guid id, string? typeName) =>
        sources.FirstOrDefault(s => s.Guid == id) ?? sources.FirstOrDefault(s => typeName is not null && s.TypeName == typeName);

    /// <summary><c>ScheduledIntervalType.Hours</c> (or <c>Hours</c>) as its value.</summary>
    private static int IntervalTypeOf(string name) => name switch
    {
        "Years" => 1,
        "Months" => 2,
        "Weeks" => 3,
        "Days" => 4,
        "Hours" => 5,
        "Minutes" => 6,
        "Seconds" => 7,
        _ => 0,
    };

    private static string? Named(string arguments, string name) =>
        Regex.Match(arguments, $@"\b{name}\s*=\s*@?""(?<value>[^""]*)""") is { Success: true } m ? m.Groups["value"].Value : null;

    /// <summary>A constant's name (<c>JobNames.Import</c>), to resolve through the index.</summary>
    private static string? NamedExpression(string arguments, string name) =>
        Regex.Match(arguments, $@"\b{name}\s*=\s*(?<value>[A-Za-z_][\w.]*)") is { Success: true } m ? m.Groups["value"].Value : null;

    private static string? Text(CSharpSourceIndex index, string? value)
    {
        if (value is null || !value.Contains('.', StringComparison.Ordinal) || value.Contains(' ', StringComparison.Ordinal))
        {
            return value;
        }
        var parts = value.Split('.');
        return index.Constants.GetValueOrDefault($"{parts[^2]}.{parts[^1]}") ?? value;
    }

    [GeneratedRegex(@"\[\s*(?:[\w.]*\.)?ScheduledPlugIn(?:Attribute)?\s*\(")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"\bIntervalType\s*=\s*(?:[\w.]*\.)?(?<type>\w+)")]
    private static partial Regex IntervalPattern();

    [GeneratedRegex(@"\bIntervalLength\s*=\s*(?<value>\d+)")]
    private static partial Regex LengthPattern();

    [GeneratedRegex(@"\bRestartable\s*=\s*true\b")]
    private static partial Regex RestartablePattern();
}
