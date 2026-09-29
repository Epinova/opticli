using OptiCli.Core.Content;

namespace OptiCli.Core.Queries;

/// <summary>
/// Sorts children the way the CMS lists them, by the parent's <c>ChildOrderRule</c>
/// (EPiServer.Filters.FilterSortOrder).
/// </summary>
public static class ChildOrder
{
    public const int CreatedDescending = 1;
    public const int CreatedAscending = 2;
    public const int Alphabetical = 3;
    public const int Index = 4;
    public const int ChangedDescending = 5;
    public const int Rank = 6;
    public const int PublishedAscending = 7;
    public const int PublishedDescending = 8;

    public static IReadOnlyList<ContentHeader> Sort(IEnumerable<ContentHeader> children, int parentRule, int? languageId)
    {
        string Name(ContentHeader h) => h.LanguageRow(languageId)?.Name ?? "";
        DateTime Created(ContentHeader h) => h.LanguageRow(languageId)?.Created ?? DateTime.MinValue;
        DateTime Changed(ContentHeader h) => h.LanguageRow(languageId)?.Saved ?? DateTime.MinValue;
        DateTime Published(ContentHeader h) => h.LanguageRow(languageId)?.StartPublish ?? DateTime.MaxValue;

        var sorted = parentRule switch
        {
            CreatedDescending => children.OrderByDescending(Created),
            CreatedAscending => children.OrderBy(Created),
            Alphabetical => children.OrderBy(Name, StringComparer.CurrentCultureIgnoreCase),
            Index or Rank => children.OrderBy(h => h.PeerOrder),
            ChangedDescending => children.OrderByDescending(Changed),
            PublishedAscending => children.OrderBy(Published),
            PublishedDescending => children.OrderByDescending(Published),
            _ => children.OrderBy(h => h.Id),
        };
        return sorted.ThenBy(h => h.Id).ToList();
    }
}
