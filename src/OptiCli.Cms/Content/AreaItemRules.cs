using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// What a ContentArea item written as a whole area keeps from the area it replaces, and which display options it may
/// name. EPiServer-free: items are their content refs (without version), display options their id, name and tag.
/// </summary>
internal static class AreaItemRules
{
    /// <summary>
    /// For each new item, the index of the current item it takes over (-1 for none): the n-th new item for some content
    /// takes over the n-th current item for that content, in area order. Inserting, removing or reordering other items
    /// doesn't change which item a block's settings stay with; of two items for the same content, the first new one
    /// takes over the first current one. Items without content (inline blocks) match nothing.
    /// </summary>
    public static int[] Match(IReadOnlyList<string?> current, IReadOnlyList<string?> wanted)
    {
        // Where the search for each content's next current item starts.
        var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matches = new int[wanted.Count];
        for (var i = 0; i < wanted.Count; i++)
        {
            matches[i] = -1;
            if (wanted[i] is not { } key)
            {
                continue;
            }
            for (var j = next.GetValueOrDefault(key); j < current.Count; j++)
            {
                if (string.Equals(current[j], key, StringComparison.OrdinalIgnoreCase))
                {
                    matches[i] = j;
                    break;
                }
            }
            next[key] = matches[i] < 0 ? current.Count : matches[i] + 1;
        }
        return matches;
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
