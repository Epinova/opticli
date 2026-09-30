using System.Globalization;
using System.Text.Json;
using OptiCli.Agent.Http;

namespace OptiCli.Agent.Content;

/// <summary>
/// Values for a page's <c>ChildSortOrder</c> and <c>SortIndex</c>. EPiServer-free: child orders are the numbers of
/// <c>EPiServer.Filters.FilterSortOrder</c>, which the writer casts.
/// </summary>
internal static class PageSorting
{
    /// <summary>The orders edit mode offers, by <c>FilterSortOrder</c> value; <c>None</c> (0) and <c>Rank</c> (6, search results) aren't child orders.</summary>
    private static readonly (string Name, int Value)[] ChildSortOrders =
    [
        ("CreatedDescending", 1),
        ("CreatedAscending", 2),
        ("Alphabetical", 3),
        ("Index", 4),
        ("ChangedDescending", 5),
        ("PublishedAscending", 7),
        ("PublishedDescending", 8),
    ];

    /// <summary>By name (<c>PublishedDescending</c>, any case) or number.</summary>
    public static int ParseChildSortOrder(JsonElement value)
    {
        var text = Text(value);
        foreach (var (name, number) in ChildSortOrders)
        {
            if (name.Equals(text, StringComparison.OrdinalIgnoreCase) || number.ToString(CultureInfo.InvariantCulture) == text)
            {
                return number;
            }
        }
        throw AgentException.Usage($"ChildSortOrder must be one of {string.Join(", ", ChildSortOrders.Select(o => o.Name))}.");
    }

    public static int ParseSortIndex(JsonElement value) =>
        int.TryParse(Text(value), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index)
            ? index
            : throw AgentException.Usage("SortIndex must be a whole number, e.g. 100.");

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Trim(),
        JsonValueKind.Number => value.GetRawText(),
        _ => "",
    };
}
