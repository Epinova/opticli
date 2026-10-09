using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// What a ContentArea item written as a whole area keeps from the area it replaces, and which display options it may
/// name. EPiServer-free: items are their content refs (without version), display options their id, name and tag.
/// </summary>
internal static class AreaItemRules
{
    /// <summary>How a new item pairs with a current one: its index (-1 for none), and whether the new item is an exact copy of it.</summary>
    public readonly record struct Pairing(int Current, bool Exact);

    /// <summary>The current item each new item takes over (-1 for none); see <see cref="Pair"/>.</summary>
    public static int[] Match(IReadOnlyList<string?> current, IReadOnlyList<string?> wanted, Func<int, int, bool>? same = null) =>
        Pair(current, wanted, same).Select(p => p.Current).ToArray();

    /// <summary>
    /// For each new item, the current item it takes over. Items are keyed by their content (its ref without version),
    /// inline blocks by their type (<see cref="InlineKey"/>); a null key matches nothing.
    /// <list type="bullet">
    /// <item>Shared blocks: the n-th new item for some content takes over the n-th current item for that content, in area
    /// order. Inserting, removing or reordering other items doesn't change which item a block's settings stay with.</item>
    /// <item>Inline blocks, which have no identity: a new item that is an exact copy of a current block (<paramref name="same"/>)
    /// takes it over (<see cref="Pairing.Exact"/>), first come first served. Of the rest, a new item takes over a current
    /// block of its type only when that is unambiguous: it is the only new item of its type left, and that the only current
    /// block of the type left. Anything else pairs with nothing.</item>
    /// </list>
    /// </summary>
    /// <param name="same">Whether new item <c>i</c> is an exact copy of current item <c>j</c> (asked for inline keys only).</param>
    /// <param name="alike">
    /// Of exact copies, whether new item <c>i</c> also has current item <c>j</c>'s name and display option: identical
    /// blocks pair with the one the item names first, so personalization stays with it.
    /// </param>
    public static Pairing[] Pair(IReadOnlyList<string?> current, IReadOnlyList<string?> wanted, Func<int, int, bool>? same = null, Func<int, int, bool>? alike = null)
    {
        var pairs = Enumerable.Repeat(new Pairing(-1, false), wanted.Count).ToArray();
        var taken = new bool[current.Count];
        bool Free(int j, string key) => !taken[j] && string.Equals(current[j], key, StringComparison.OrdinalIgnoreCase);
        // Exact copies that are alike first, then any exact copy.
        foreach (var strict in alike is null ? [false] : new[] { true, false })
        {
            for (var i = 0; i < wanted.Count && same is not null; i++)
            {
                if (pairs[i].Current >= 0 || wanted[i] is not { } key || !IsInline(key))
                {
                    continue;
                }
                for (var j = 0; j < current.Count; j++)
                {
                    if (Free(j, key) && (!strict || alike!(i, j)) && same(i, j))
                    {
                        (pairs[i], taken[j]) = (new Pairing(j, true), true);
                        break;
                    }
                }
            }
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (pairs[i].Current >= 0 || wanted[i] is not { } key)
            {
                continue;
            }
            if (IsInline(key))
            {
                var left = Enumerable.Range(0, current.Count).Where(j => Free(j, key)).ToList();
                var others = Enumerable.Range(0, wanted.Count).Count(k => pairs[k].Current < 0 && string.Equals(wanted[k], key, StringComparison.OrdinalIgnoreCase));
                if (left.Count == 1 && others == 1)
                {
                    (pairs[i], taken[left[0]]) = (new Pairing(left[0], false), true);
                }
                continue;
            }
            for (var j = 0; j < current.Count; j++)
            {
                if (Free(j, key))
                {
                    (pairs[i], taken[j]) = (new Pairing(j, false), true);
                    break;
                }
            }
        }
        return pairs;
    }

    private const string InlinePrefix = "inline:";

    private static bool IsInline(string key) => key.StartsWith(InlinePrefix, StringComparison.Ordinal);

    /// <summary>The key of an inline block of the content type <paramref name="typeId"/>, for <see cref="Match"/>.</summary>
    public static string InlineKey(int typeId) => InlinePrefix + typeId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A property name that names one item of a ContentArea (<c>MainArea[2]</c>, zero-based), as <c>where-used</c> and
    /// <c>find --where</c> show an inline block's place; null for a plain name.
    /// </summary>
    /// <exception cref="AgentException"><c>usage</c> for brackets that don't hold a position.</exception>
    public static (string Property, int Index)? Indexed(string name)
    {
        var open = name.IndexOf('[');
        if (open < 0 && !name.Contains(']'))
        {
            return null;
        }
        if (open > 0 && name.EndsWith(']') && int.TryParse(name.AsSpan(open + 1, name.Length - open - 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            return (name[..open].Trim(), index);
        }
        throw AgentException.Usage($"'{name}' is neither a property name nor one item of a ContentArea, like MainArea[2] (zero-based).");
    }

    /// <summary>
    /// The item's personalization: what it gives, else what the item it takes over has. <c>""</c> (group) and
    /// <c>[]</c> (visitor groups) remove it.
    /// </summary>
    public static (string? Group, IReadOnlyList<string> VisitorGroups) Personalization(
        AreaItemValue item, string? currentGroup, IEnumerable<string>? currentVisitorGroups)
    {
        var group = item.Group ?? currentGroup;
        var visitorGroups = item.VisitorGroups ?? currentVisitorGroups ?? [];
        return (
            string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
            visitorGroups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>A display option the site registers (<c>EPiServer.Web.DisplayOption</c>).</summary>
    public sealed record Option(string Id, string? Name, string? Tag);

    /// <summary>
    /// The id of the display option <paramref name="input"/> names: its id (any case), else its name or rendering tag.
    /// An unknown one is refused, as edit mode offers only the registered ones.
    /// </summary>
    public static string DisplayOption(string input, IReadOnlyList<Option> options)
    {
        var text = input.Trim();
        var match = options.FirstOrDefault(o => o.Id.Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(o => string.Equals(o.Name, text, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(o => string.Equals(o.Tag, text, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match.Id;
        }
        var ids = options.Select(o => o.Id).ToList();
        if (ids.Count == 0)
        {
            throw AgentException.Usage($"No display option '{text}': the site registers none (EPiServer.Web.DisplayOptions).",
                "Leave displayOption out.");
        }
        var suggestion = Suggestions.DidYouMean(text, ids);
        var list = $"Display options: {string.Join(", ", ids)}.";
        throw AgentException.Usage($"No display option '{text}'.", suggestion is null ? list : $"{suggestion} {list}");
    }

    /// <summary>
    /// Visitor group ids must name a visitor group; anything that isn't a GUID is a role name, which the CMS also takes
    /// there, and is left as it is.
    /// </summary>
    /// <param name="name">The group's name by id; null for no such group.</param>
    /// <exception cref="AgentException"><c>usage</c> for an id no visitor group has.</exception>
    public static void RequireVisitorGroups(IEnumerable<string> visitorGroups, Func<Guid, string?> name)
    {
        var unknown = visitorGroups.Where(g => Guid.TryParse(g, out var id) && name(id) is null).ToList();
        if (unknown.Count > 0)
        {
            throw AgentException.Usage($"No visitor group has the id {string.Join(", ", unknown)}.",
                "Visitor group ids are in get's visitorGroups (with visitorGroupNames), and in admin mode's Visitor Groups.");
        }
    }
}
