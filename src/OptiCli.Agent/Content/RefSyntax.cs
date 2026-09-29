using System.Globalization;

namespace OptiCli.Agent.Content;

/// <summary>A ref as the agent accepts it: content id, optional version id, or a content GUID.</summary>
internal readonly record struct ParsedRef(int Id, int? Version, Guid? Guid);

/// <summary>Purely syntactic ref parsing; whether the content exists is <see cref="ContentLocator"/>'s job.</summary>
internal static class RefSyntax
{
    public const string Description = "a content id (123), a version (123_456) or a content GUID";

    public static bool TryParse(string? input, out ParsedRef result)
    {
        result = default;
        var value = input?.Trim() ?? "";

        if (Guid.TryParse(value, out var guid) && guid != System.Guid.Empty)
        {
            result = new ParsedRef(0, null, guid);
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
}
