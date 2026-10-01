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

    /// <summary>What children are sorted by: the item's id and sort index, and the name and dates of the branch shown.</summary>
    public sealed record Key(int Id, int PeerOrder, string? Name, DateTime? Created, DateTime? Saved, DateTime? StartPublish);

    /// <summary>The sort key of <paramref name="child"/> in the branch <see cref="ContentHeader.LanguageRow"/> picks.</summary>
    public static Key KeyOf(ContentHeader child, int? languageId)
    {
        var row = child.LanguageRow(languageId);
        return new Key(child.Id, child.PeerOrder, row?.Name, row?.Created, row?.Saved, row?.StartPublish);
    }

    public static IReadOnlyList<ContentHeader> Sort(IEnumerable<ContentHeader> children, int parentRule, int? languageId) =>
        Sort(children, h => KeyOf(h, languageId), parentRule);

    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> children, Func<T, Key> keyOf, int parentRule)
    {
        var keyed = children.Select(c => (Item: c, Key: keyOf(c)));
        string Name((T, Key Key) c) => c.Key.Name ?? "";
        DateTime Created((T, Key Key) c) => c.Key.Created ?? DateTime.MinValue;
        DateTime Changed((T, Key Key) c) => c.Key.Saved ?? DateTime.MinValue;
        DateTime Published((T, Key Key) c) => c.Key.StartPublish ?? DateTime.MaxValue;

        var sorted = parentRule switch
        {
            CreatedDescending => keyed.OrderByDescending(Created),
            CreatedAscending => keyed.OrderBy(Created),
            Alphabetical => keyed.OrderBy(Name, StringComparer.CurrentCultureIgnoreCase),
            _ when ByIndex(parentRule) => keyed.OrderBy(c => c.Key.PeerOrder),
            ChangedDescending => keyed.OrderByDescending(Changed),
            PublishedAscending => keyed.OrderBy(Published),
            PublishedDescending => keyed.OrderByDescending(Published),
            _ => keyed.OrderBy(c => c.Key.Id),
        };
        return sorted.ThenBy(c => c.Key.Id).Select(c => c.Item).ToList();
    }
}
