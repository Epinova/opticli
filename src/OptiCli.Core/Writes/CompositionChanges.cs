using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>One node of a Visual Builder composition that a write added, removed, moved or changed.</summary>
/// <param name="Change"><c>added</c>, <c>removed</c>, <c>moved</c> or <c>changed</c> (a moved node may have changed too).</param>
/// <param name="NodeType"><c>section</c>, <c>row</c>, <c>column</c>, <c>element</c>, <c>component</c> (a section-enabled block in an outline) or <c>composition</c> (its own display template).</param>
public sealed record CompositionChange(string Change, string NodeType, string? Key, string? Name, string? Type)
{
    /// <summary>For an added or moved node: its parent's key, or <c>root</c>.</summary>
    public string? In { get; init; }

    /// <summary>For an added or moved node: its zero-based position there.</summary>
    public int? At { get; init; }

    /// <summary>A shared block placed by reference.</summary>
    public string? Ref { get; init; }

    /// <summary>For an added node: how many nodes it holds (rows, columns, elements), added with it.</summary>
    public int? Holds { get; init; }

    /// <summary>What changed: <c>name</c>, <c>displayTemplate</c>, <c>displaySettings</c>, <c>ref</c>, and each property of an inline block (<c>properties.Heading</c>).</summary>
    public IReadOnlyList<CompositionFieldChange>? Changes { get; init; }
}

/// <param name="Field"><c>name</c>, <c>displayTemplate</c>, <c>displaySettings</c>, <c>ref</c> or <c>properties.&lt;Name&gt;</c>.</param>
/// <param name="Before">Omitted when it had none.</param>
/// <param name="After">Omitted when it has none now.</param>
public sealed record CompositionFieldChange(string Field, JsonNode? Before, JsonNode? After);

/// <summary>
/// CMS 13: a write's change of a Visual Builder composition, node by node, instead of the whole composition before and
/// after (which the site agent reports as the <c>composition</c> pseudo-property): keys are compared, so a node keeps its
/// identity wherever it moves.
/// </summary>
public static class CompositionChanges
{
    public const string Root = "root";

    private sealed record Place(JsonObject Node, string Parent, int Index, bool Outline);

    /// <summary>The nodes that differ between <paramref name="before"/> and <paramref name="after"/>, in the order of <paramref name="after"/> (removals last).</summary>
    public static IReadOnlyList<CompositionChange> Compare(JsonElement? before, JsonElement? after)
    {
        var old = Flatten(Node(before));
        var now = Flatten(Node(after));
        var changes = new List<CompositionChange>();
        if (Styles(Node(before), Node(after)) is { Count: > 0 } rootChanges)
        {
            changes.Add(new CompositionChange("changed", "composition", null, null, null) { Changes = rootChanges });
        }

        var moved = Moved(old, now);
        foreach (var (key, place) in now)
        {
            if (!old.TryGetValue(key, out var was))
            {
                if (now.ContainsKey(place.Parent) && !old.ContainsKey(place.Parent))
                {
                    // Inside a node added with it.
                    continue;
                }
                changes.Add(Describe("added", key, place) with { In = place.Parent, At = place.Index, Holds = Count(place.Node) is > 0 and var holds ? holds : null });
                continue;
            }
            var differences = Differences(was.Node, place.Node);
            if (moved.Contains(key))
            {
                changes.Add(Describe("moved", key, place) with { In = place.Parent, At = place.Index, Changes = differences.Count > 0 ? differences : null });
            }
            else if (differences.Count > 0)
            {
                changes.Add(Describe("changed", key, place) with { Changes = differences });
            }
        }
        foreach (var (key, place) in old)
        {
            if (!now.ContainsKey(key) && (!old.ContainsKey(place.Parent) || now.ContainsKey(place.Parent)))
            {
                changes.Add(Describe("removed", key, place) with { Holds = Count(place.Node) is > 0 and var holds ? holds : null });
            }
        }
        return changes;
    }

    /// <summary>
    /// The agent's changes with the <c>composition</c> pseudo-property replaced by <see cref="Compare"/>'s list; null when
    /// the write didn't change a composition.
    /// </summary>
    public static (IReadOnlyList<PropertyChange> Changes, IReadOnlyList<CompositionChange>? Composition) Split(IReadOnlyList<PropertyChange> changes)
    {
        var composition = changes.FirstOrDefault(c => c.Property.Equals(CompositionInput.Field, StringComparison.Ordinal));
        return composition is null
            ? (changes, null)
            : (changes.Where(c => c != composition).ToList(), Compare(composition.Before, composition.After));
    }

    private static CompositionChange Describe(string change, string key, Place place) => new(
        change,
        NodeType(place),
        key,
        Text(place.Node["name"]),
        Text(place.Node["type"]))
    {
        Ref = Text(place.Node["ref"]),
    };

    private static string NodeType(Place place) => Text(place.Node["nodeType"]) switch
    {
        "component" => place.Outline ? "component" : "element",
        null => "node",
        var other => other,
    };

    /// <summary>Every node with a key, with its parent's key (or <c>root</c>) and position.</summary>
    private static Dictionary<string, Place> Flatten(JsonObject? root)
    {
        var result = new Dictionary<string, Place>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonObject node, string parent, bool outline)
        {
            var index = 0;
            foreach (var child in node["nodes"] as JsonArray ?? [])
            {
                if (child is JsonObject childNode && Text(childNode["key"]) is { } key)
                {
                    result[key] = new Place(childNode, parent, index, outline);
                    Walk(childNode, key, outline: false);
                }
                index++;
            }
        }
        if (root is not null)
        {
            // An experience's composition holds sections; a section's own holds rows.
            var outline = (root["nodes"] as JsonArray)?.OfType<JsonObject>().All(n => Text(n["nodeType"]) is "section" or "component") ?? true;
            Walk(root, Root, outline);
        }
        return result;
    }

    /// <summary>Nodes in both whose parent changed, or whose order among the siblings they kept changed.</summary>
    private static HashSet<string> Moved(Dictionary<string, Place> old, Dictionary<string, Place> now)
    {
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, place) in now)
        {
            if (old.TryGetValue(key, out var was) && !string.Equals(was.Parent, place.Parent, StringComparison.OrdinalIgnoreCase))
            {
                moved.Add(key);
            }
        }
        foreach (var group in now.Where(p => old.TryGetValue(p.Key, out var was) && string.Equals(was.Parent, p.Value.Parent, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Value.Parent, StringComparer.OrdinalIgnoreCase))
        {
            var after = group.OrderBy(p => p.Value.Index).Select(p => p.Key).ToList();
            var before = after.OrderBy(k => old[k].Index).ToList();
            // The fewest nodes that moved: those outside the longest run kept in order.
            var kept = LongestKept(before, after);
            moved.UnionWith(after.Where(k => !kept.Contains(k)));
        }
        return moved;
    }

    /// <summary>The longest common subsequence of two orders of the same keys.</summary>
    private static HashSet<string> LongestKept(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var lengths = new int[before.Count + 1, after.Count + 1];
        for (var i = before.Count - 1; i >= 0; i--)
        {
            for (var j = after.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(before[i], after[j], StringComparison.OrdinalIgnoreCase)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0, j = 0; i < before.Count && j < after.Count;)
        {
            if (string.Equals(before[i], after[j], StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(before[i]);
                i++;
                j++;
            }
            // On a tie the node that comes first now counts as the one moved: "move X to the top" names X.
            else if (lengths[i, j + 1] >= lengths[i + 1, j])
            {
                j++;
            }
            else
            {
                i++;
            }
        }
        return kept;
    }

    private static List<CompositionFieldChange> Differences(JsonObject before, JsonObject after)
    {
        var result = new List<CompositionFieldChange>();
        foreach (var field in new[] { "name", "ref" })
        {
            Compare(result, field, before[field], after[field]);
        }
        result.AddRange(Styles(before, after));
        var oldValues = before["properties"] as JsonObject ?? [];
        var newValues = after["properties"] as JsonObject ?? [];
        foreach (var name in newValues.Select(p => p.Key).Concat(oldValues.Select(p => p.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Compare(result, $"properties.{name}", oldValues[name], newValues[name]);
        }
        return result;
    }

    private static List<CompositionFieldChange> Styles(JsonObject? before, JsonObject? after)
    {
        var result = new List<CompositionFieldChange>();
        foreach (var field in new[] { "displayTemplate", "displaySettings" })
        {
            Compare(result, field, before?[field], after?[field]);
        }
        return result;
    }

    private static void Compare(List<CompositionFieldChange> result, string name, JsonNode? before, JsonNode? after)
    {
        if (!JsonNode.DeepEquals(before, after))
        {
            result.Add(new CompositionFieldChange(name, before?.DeepClone(), after?.DeepClone()));
        }
    }

    /// <summary>How many nodes a node holds, all the way down.</summary>
    private static int Count(JsonObject node) =>
        (node["nodes"] as JsonArray ?? []).OfType<JsonObject>().Sum(child => 1 + Count(child));

    private static JsonObject? Node(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText()) as JsonObject : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
