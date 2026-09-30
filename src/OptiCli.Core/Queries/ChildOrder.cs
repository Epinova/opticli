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

    /// <summary>The rule's <c>FilterSortOrder</c> name, as <c>get</c> shows it and <c>set ChildSortOrder=...</c> takes it.</summary>
    public static string Name(int rule) => rule switch
    {
        CreatedDescending => nameof(CreatedDescending),
        CreatedAscending => nameof(CreatedAscending),
        Alphabetical => nameof(Alphabetical),
        Index => nameof(Index),
        ChangedDescending => nameof(ChangedDescending),
        Rank => nameof(Rank),
        PublishedAscending => nameof(PublishedAscending),
        PublishedDescending => nameof(PublishedDescending),
        0 => "None",
        _ => rule.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Whether children under this rule are listed by their sort index.</summary>
    public static bool ByIndex(int rule) => rule is Index or Rank;

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
            _ when ByIndex(parentRule) => children.OrderBy(h => h.PeerOrder),
            ChangedDescending => children.OrderByDescending(Changed),
            PublishedAscending => children.OrderBy(Published),
            PublishedDescending => children.OrderByDescending(Published),
            _ => children.OrderBy(h => h.Id),
        };
        return sorted.ThenBy(h => h.Id).ToList();
    }
}
