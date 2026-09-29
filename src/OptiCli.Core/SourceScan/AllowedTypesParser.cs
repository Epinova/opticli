using System.Text.RegularExpressions;

namespace OptiCli.Core.SourceScan;

/// <summary>The type names an <c>[AllowedTypes]</c> attribute lists.</summary>
/// <param name="Allowed">Types (or base classes/interfaces) the property accepts; empty means any.</param>
/// <param name="Restricted">Types it refuses even though they match <paramref name="Allowed"/>.</param>
public sealed record AllowedTypesDeclaration(IReadOnlyList<string> Allowed, IReadOnlyList<string> Restricted);

/// <summary>
/// Reads <c>[AllowedTypes]</c> from a property's attribute text. Handles the attribute's constructors
/// (<c>params Type[]</c>, and <c>(Type[] allowed, Type[] restricted)</c> with <c>new[] { ... }</c> or
/// <c>[ ... ]</c> arrays) and its named arguments. The CMS keeps these rules in code only.
/// </summary>
public static partial class AllowedTypesParser
{
    /// <returns>Null when the attributes have no <c>[AllowedTypes]</c>.</returns>
    public static AllowedTypesDeclaration? Parse(string attributes)
    {
        var start = AttributePattern().Match(attributes);
        if (!start.Success)
        {
            return null;
        }
        var open = start.Index + start.Length - 1;
        var close = CSharpText.FindClosing(attributes, open, '(', ')');
        var arguments = CSharpText.SplitTopLevel(attributes[(open + 1)..Math.Min(close, attributes.Length)]).Where(a => a.Trim().Length > 0);

        var allowed = new List<string>();
        var restricted = new List<string>();
        var positional = 0;
        foreach (var argument in arguments)
        {
            var named = NamedPattern().Match(argument);
            var names = TypeNames(named.Success ? argument[named.Length..] : argument);
            if (named.Success)
            {
                (named.Groups["name"].Value switch
                {
                    "AllowedTypes" => allowed,
                    "RestrictedTypes" => restricted,
                    _ => null,
                })?.AddRange(names);
            }
            else if (IsArray(argument))
            {
                (positional++ == 0 ? allowed : restricted).AddRange(names);
            }
            else
            {
                // params Type[]: every positional typeof(...) is an allowed type.
                allowed.AddRange(names);
            }
        }
        return new AllowedTypesDeclaration(allowed.Distinct().ToList(), restricted.Distinct().ToList());
    }

    private static bool IsArray(string argument)
    {
        var trimmed = argument.TrimStart();
        return trimmed.StartsWith('[') || trimmed.StartsWith("new", StringComparison.Ordinal);
    }

    private static IEnumerable<string> TypeNames(string expression) =>
        TypeOfPattern().Matches(expression).Select(m => CSharpText.SimpleTypeName(m.Groups["type"].Value));

    [GeneratedRegex(@"[\[,]\s*(?:[\w.]*\.)?AllowedTypes(?:Attribute)?\s*\(")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"^\s*(?<name>\w+)\s*=(?!=)")]
    private static partial Regex NamedPattern();

    [GeneratedRegex(@"\btypeof\s*\(\s*(?<type>[\w.<>, ]+?)\s*\)")]
    private static partial Regex TypeOfPattern();
}
