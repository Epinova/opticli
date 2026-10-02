using System.Text.Json;

namespace OptiCli.Cms.Content;

/// <summary>
/// A page's shortcut (edit mode's Settings > Shortcut) as a request gives it. EPiServer-free: <see cref="Type"/> is the
/// name of <c>EPiServer.Core.PageShortcutType</c>, which the writer maps.
/// </summary>
/// <param name="Type"><c>Normal</c> (no shortcut), <c>Shortcut</c>, <c>External</c>, <c>FetchData</c> or <c>Inactive</c>.</param>
/// <param name="To">The content a shortcut or fetch-data page points at, or an external link's page (with <paramref name="Anchor"/>).</param>
/// <param name="Url">An external link's URL.</param>
/// <param name="Target">The window to open it in (<c>_blank</c>, <c>_top</c>), or null for the same window.</param>
internal sealed record ShortcutValue(string Type, string? To = null, string? Url = null, string? Anchor = null, string? Target = null)
{
    public const string Normal = "Normal";
    public const string Shortcut = "Shortcut";
    public const string External = "External";
    public const string FetchData = "FetchData";
    public const string Inactive = "Inactive";

    private static readonly string[] Types = [Normal, Shortcut, External, FetchData, Inactive];

    public const string Syntax =
        "Shortcut takes a content ref (a shortcut to that page), a URL (an external link), \"inactive\", nothing (no shortcut), "
        + "or {\"type\": \"shortcut|external|fetchData|inactive|normal\", \"to\": \"<ref>\", \"url\": \"...\", \"anchor\": \"...\", \"target\": \"_blank\"}.";

    /// <summary>Shortcut types as the snapshot shows them: <c>shortcut</c>, <c>fetchData</c>, ...</summary>
    public static string CamelCase(string type) => char.ToLowerInvariant(type[0]) + type[1..];

    public static ShortcutValue Parse(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return new ShortcutValue(Normal);
            case JsonValueKind.Number:
                return new ShortcutValue(Shortcut, To: value.GetRawText());
            case JsonValueKind.String:
                var text = value.GetString()!.Trim();
                if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return new ShortcutValue(Normal);
                }
                if (TypeName(text) is { } bare && bare is Normal or Inactive)
                {
                    return new ShortcutValue(bare);
                }
                return RefSyntax.TryParse(text, out _) ? new ShortcutValue(Shortcut, To: text) : new ShortcutValue(External, Url: Link(text));
            case JsonValueKind.Object:
                return FromObject(value);
            default:
                throw AgentException.Usage(Syntax);
        }
    }

    private static ShortcutValue FromObject(JsonElement value)
    {
        string? type = null, to = null, url = null, anchor = null, target = null;
        foreach (var field in value.EnumerateObject())
        {
            switch (field.Name.ToLowerInvariant())
            {
                case "type":
                    type = TypeName(Text(field.Value, "type")) ?? throw AgentException.Usage($"Shortcut type must be one of {string.Join(", ", Types.Select(CamelCase))}.");
                    break;
                case "to" or "ref":
                    // As get shows it ({"ref": ...}) or as a plain ref.
                    to = field.Value.ValueKind == JsonValueKind.Object && field.Value.TryGetProperty("ref", out var inner) ? Text(inner, "to.ref") : Text(field.Value, "to");
                    break;
                case "url":
                    url = Text(field.Value, "url");
                    break;
                case "anchor":
                    anchor = Text(field.Value, "anchor")?.TrimStart('#');
                    break;
                case "target":
                    target = Text(field.Value, "target");
                    break;
                default:
                    throw AgentException.Usage($"Unknown shortcut field '{field.Name}'.", Syntax);
            }
        }

        type ??= url is not null ? External : to is not null ? Shortcut : throw AgentException.Usage("A shortcut object needs a type, a to or a url.", Syntax);
        if (type == External && to is not null && url is not null && IsPermanentLink(url))
        {
            // As get shows an external link to a page: the stored permanent link next to the page it names.
            url = null;
        }
        if (url?.IndexOf('#', StringComparison.Ordinal) is >= 0 and var hash && anchor is not null)
        {
            anchor = url[(hash + 1)..] == anchor
                ? null
                : throw AgentException.Usage($"The url already has the anchor #{url[(hash + 1)..]}; drop \"anchor\" or the url's #{url[(hash + 1)..]}.");
        }
        switch (type)
        {
            case Shortcut or FetchData when to is null:
                throw AgentException.Usage($"A {CamelCase(type)} shortcut needs \"to\": the page it points at.");
            case Shortcut or FetchData when url is not null || anchor is not null:
                throw AgentException.Usage($"A {CamelCase(type)} shortcut takes \"to\", not \"url\" or \"anchor\"; an external link to a page's anchor is {{\"type\": \"external\", \"to\": \"<ref>\", \"anchor\": \"...\"}}.");
            case External when (to is null) == (url is null):
                throw AgentException.Usage("An external link needs either \"url\" or \"to\" (a page, optionally with \"anchor\").");
            case Normal or Inactive when to is not null || url is not null || anchor is not null || target is not null:
                throw AgentException.Usage($"A {CamelCase(type)} shortcut takes no other fields.");
        }
        return new ShortcutValue(type, to, url is null ? null : Link(url), anchor is { Length: > 0 } ? anchor : null, target is { Length: > 0 } ? target : null);
    }

    private static bool IsPermanentLink(string url) => url.TrimStart().StartsWith("~/link/", StringComparison.OrdinalIgnoreCase);

    private static string? TypeName(string? text) => Types.FirstOrDefault(t => t.Equals(text?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Absolute URLs, site-relative paths, anchors and stored permanent links; anything else is a typo.</summary>
    private static string Link(string url)
    {
        var trimmed = url.Trim();
        return trimmed.Contains("://", StringComparison.Ordinal) || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('/') || trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith('#')
            ? trimmed
            : throw AgentException.Usage($"'{url}' is neither a content ref nor a URL.", Syntax);
    }

    private static string? Text(JsonElement value, string field) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => throw AgentException.Usage($"Shortcut field '{field}' must be a string."),
    };
}
