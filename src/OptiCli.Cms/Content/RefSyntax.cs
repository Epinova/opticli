using System.Globalization;
using System.Text.RegularExpressions;

namespace OptiCli.Cms.Content;

/// <summary>A ref as the agent accepts it: content id, optional version id, a content GUID, or provider content.</summary>
/// <param name="Provider">For <c>63__provider</c>: the content provider's name, with <see cref="Id"/> its mapped id.</param>
internal readonly record struct ParsedRef(int Id, int? Version, Guid? Guid, string? Provider = null);

/// <summary>Purely syntactic ref parsing; whether the content exists is <see cref="ContentLocator"/>'s job.</summary>
internal static partial class RefSyntax
{
    public const string Description = "a content id (123), a version (123_456), a content GUID or content provider content (63__provider)";

    public static bool TryParse(string? input, out ParsedRef result)
    {
        result = default;
        var value = input?.Trim() ?? "";

        if (Guid.TryParse(value, out var guid) && guid != System.Guid.Empty)
        {
            result = new ParsedRef(0, null, guid);
            return true;
        }

        // ContentReference.ToString() of provider content: id, an empty version, the provider name.
        if (ProviderPattern().Match(value) is { Success: true } provided)
        {
            if (!TryParsePositive(provided.Groups["id"].Value, out var mapped))
            {
                return false;
            }
            result = new ParsedRef(mapped, null, null, provided.Groups["provider"].Value);
            return true;
        }

        var parts = value.Split('_');
        if (parts.Length is < 1 or > 2 || !TryParsePositive(parts[0], out var id))
        {
            return false;
        }
        if (parts.Length == 1)
        {
            result = new ParsedRef(id, null, null);
            return true;
        }
        if (!TryParsePositive(parts[1], out var version))
        {
            return false;
        }
        result = new ParsedRef(id, version, null);
        return true;
    }

    private static bool TryParsePositive(string digits, out int number) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    [GeneratedRegex(@"^(?<id>[0-9]+)__(?<provider>[A-Za-z0-9][A-Za-z0-9.-]*)$")]
    private static partial Regex ProviderPattern();
}
