using System.Globalization;
using System.Text.RegularExpressions;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Refs;

public static partial class ContentRefParser
{
    public const string Syntax =
        "A ref is a content id (123), id_version (123_456), a content GUID, a URL/path (/en/about/ or https://host/en/about/), or content provider content as get prints it (63__provider).";

    /// <exception cref="UsageException">The input is not a ref.</exception>
    public static ContentRef Parse(string? input) =>
        TryParse(input, out var result, out var error)
            ? result
            : throw new UsageException(error, Syntax);

    public static bool TryParse(string? input, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ContentRef? result, out string error)
    {
        result = null;
        error = "";
        var value = input?.Trim() ?? "";

        if (value.Length == 0)
        {
            error = "Empty ref.";
            return false;
        }

        if (IdPattern().Match(value) is { Success: true } id)
        {
            if (!TryParsePositive(id.Groups["id"].Value, out var contentId)
                || (id.Groups["version"].Success && !TryParsePositive(id.Groups["version"].Value, out _)))
            {
                error = $"'{value}' is not a valid content id: ids and versions are positive 32-bit integers.";
                return false;
            }
            int? version = id.Groups["version"].Success ? int.Parse(id.Groups["version"].Value, CultureInfo.InvariantCulture) : null;
            result = ContentRef.ForId(contentId, version);
            return true;
        }

        if (ProviderPattern().Match(value) is { Success: true } provided)
        {
            if (!TryParsePositive(provided.Groups["id"].Value, out var mappedId))
            {
                error = $"'{value}' is not a valid content provider ref: the id is a positive 32-bit integer.";
                return false;
            }
            result = ContentRef.ForProvider(mappedId, provided.Groups["provider"].Value);
            return true;
        }

        if (Guid.TryParse(value, out var guid))
        {
            result = ContentRef.ForGuid(guid);
            return true;
        }

        if (PermanentLinkPattern().Match(value) is { Success: true } link && Guid.TryParse(link.Groups["guid"].Value, out var linked))
        {
            result = ContentRef.ForGuid(linked);
            return true;
        }

        if (value.StartsWith('/') || value.StartsWith("~/", StringComparison.Ordinal))
        {
            result = ContentRef.ForUrl(value.StartsWith('~') ? value[1..] : value);
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            result = ContentRef.ForUrl(value);
            return true;
        }

        error = VersionedProviderPattern().Match(value) is { Success: true } versioned
            ? $"'{value}' names a version of content provider content, which opticli can't address; use {versioned.Groups["id"].Value}__{versioned.Groups["provider"].Value}."
            : IdentifierPattern().IsMatch(value)
                ? $"'{value}' is not a content ref. If it is a content type, list its content with `opticli find --type {value}`."
                : $"'{value}' is not a content ref.";
        return false;
    }

    private static bool TryParsePositive(string digits, out int number) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^(?<id>[0-9]+)(?:_(?<version>[0-9]+))?$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// <c>ContentReference.ToString()</c> of provider content: id, an empty version, then the provider name. Provider
    /// content has no versions opticli can address, so <c>63_5__provider</c> is not accepted.
    /// </summary>
    [GeneratedRegex(@"^(?<id>[0-9]+)__(?<provider>[A-Za-z0-9][A-Za-z0-9.-]*)$")]
    private static partial Regex ProviderPattern();

    [GeneratedRegex(@"^(?<id>[0-9]+)_[0-9]+__?(?<provider>[A-Za-z0-9][A-Za-z0-9.-]*)$")]
    private static partial Regex VersionedProviderPattern();

    /// <summary>The permanent-link form CMS stores in rich text and link properties.</summary>
    [GeneratedRegex(@"^~?/link/(?<guid>[0-9a-fA-F-]{32,36})\.aspx", RegexOptions.IgnoreCase)]
    private static partial Regex PermanentLinkPattern();
}
