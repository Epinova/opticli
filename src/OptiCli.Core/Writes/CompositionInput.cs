using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Properties;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// CMS 13: a Visual Builder composition as a write gives it (<c>set</c>/<c>create</c> with <c>composition</c> in the values,
/// <c>composition add --node</c>, a plan), turned into what the site agent takes (<see cref="CompositionNodeValue"/>).
/// </summary>
/// <remarks>
/// <para>It takes <c>get</c>'s shape, so a composition read with <c>get</c> can be written back as it is or changed:
/// <c>sections</c> → <c>rows</c> → <c>columns</c> → <c>elements</c> (or <c>nodes</c> with each one's <c>nodeType</c>), and per
/// node <c>key</c>, <c>name</c>, <c>type</c>, <c>displayTemplate</c>, <c>displaySettings</c>, <c>properties</c>; a shared block is
/// <c>content: {ref}</c> or just <c>ref</c>. What <c>get</c> adds for reading is ignored (<c>layout</c>, <c>culture</c>,
/// <c>inline</c>); property values may be given as <c>get</c> shows them (<c>{type, value}</c>, a reference as <c>{ref, ...}</c>)
/// or as <c>set</c> takes them. A value <c>get</c> cut short (<c>truncated</c>) is refused: read it with <c>--full</c>.</para>
/// <para>Also the shape writes report it in (<c>nodes</c> with <c>nodeType</c>), so a write's <c>after</c> can be sent back.</para>
/// </remarks>
public static class CompositionInput
{
    /// <summary>The name a composition goes under in a property map.</summary>
    public const string Field = Compositions.Field;

    private static readonly string[] Lists = ["sections", "rows", "columns", "elements", "nodes"];

    private static readonly HashSet<string> NodeFields = new(StringComparer.Ordinal)
    {
        "nodeType", "key", "name", "type", "inline", "content", "ref", "blueprint", "displayTemplate", "displaySettings", "properties",
        "sections", "rows", "columns", "elements", "nodes",
    };

    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "layout", "culture", "displayTemplate", "displaySettings", "sections", "rows", "nodes", "unplaced",
    };

    /// <summary>
    /// Takes <see cref="Field"/> out of a property map of content of <paramref name="typeId"/>, when the type has a
    /// composition (and no property of that name). A string value is read as JSON (<c>composition=@file.json</c>).
    /// </summary>
    /// <returns>The other properties (null when none are left) and the composition (null when not given).</returns>
    /// <exception cref="UsageException">A composition for content that has none, or one that isn't a JSON object.</exception>
    public static (JsonObject? Properties, JsonObject? Composition) Split(CmsModel model, int typeId, JsonObject? properties)
    {
        if (properties is null || properties.Select(p => p.Key).FirstOrDefault(k => k.Equals(Field, StringComparison.OrdinalIgnoreCase)) is not { } key
            || model.PropertiesOf(typeId).Any(p => p.Name.Equals(Field, StringComparison.OrdinalIgnoreCase)))
        {
            return (properties, null);
        }
        if (!Compositions.IsLayouted(model, typeId))
        {
            throw new UsageException($"{model.TypeName(typeId)} has no Visual Builder composition; only experiences and sections (CMS 13) have one.",
                "Leave composition out of the values.");
        }
        var rest = (JsonObject)properties.DeepClone();
        var value = rest[key];
        rest.Remove(key);
        var composition = value switch
        {
            JsonObject obj => obj,
            JsonValue text when text.TryGetValue<string>(out var json) => ParseText(json),
            _ => throw new UsageException("composition must be a JSON object, as get shows it.", Hint),
        };
        return (rest.Count == 0 ? null : rest, (JsonObject)composition.DeepClone());
    }

    public const string Hint = "Give it as get shows it: {\"sections\": [{\"type\": \"<SectionType>\", \"rows\": [{\"columns\": [{\"elements\": [{\"type\": \"<ElementType>\", \"properties\": {...}}]}]}]}]}; a node with a key keeps its block.";

    private static JsonObject ParseText(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new UsageException("composition must be a JSON object, as get shows it.", Hint);
        }
        catch (JsonException ex)
        {
            throw new UsageException($"composition is not valid JSON: {ex.Message}", Hint);
        }
    }

    /// <summary>Turns refs (ids, GUIDs, URLs) and blueprints (refs or names) into what the agent takes.</summary>
    /// <param name="Ref">A shared block's ref → the content ref the agent takes.</param>
    /// <param name="Blueprint">A section blueprint (ref or name) → its content GUID.</param>
    public sealed record Resolvers(Func<string, Task<string>> Ref, Func<string, Task<Guid>> Blueprint);

    /// <summary>The whole composition of content of <paramref name="ownerTypeId"/>.</summary>
    /// <exception cref="UsageException">Unknown fields, a property that isn't the block type's, a value <c>get</c> cut short.</exception>
    public static async Task<CompositionNodeValue> RootAsync(CmsModel model, JsonObject composition, Resolvers resolve)
    {
        const string where = "composition";
        Unknown(composition, RootFields, where);
        if (composition["unplaced"] is JsonArray { Count: > 0 })
        {
            throw new UsageException($"{where}.unplaced lists stored items no node shows; they can't be written back.",
                "Leave unplaced out (the items are dropped), or put them in the composition as nodes.");
        }
        return new CompositionNodeValue
        {
            DisplayTemplate = Text(composition["displayTemplate"], $"{where}.displayTemplate"),
            DisplaySettings = Settings(composition["displaySettings"], $"{where}.displaySettings"),
            Nodes = await ChildrenAsync(model, composition, where, resolve) ?? [],
        };
    }

    /// <summary>One node (with its children), as <c>composition add --node</c> or a plan's <c>node</c> gives it.</summary>
    /// <param name="nodeType">The node type the command names (<c>section</c>, <c>row</c>, <c>column</c>, <c>element</c>), if any.</param>
    public static Task<CompositionNodeValue> NodeAsync(CmsModel model, JsonObject node, string? nodeType, string where, Resolvers resolve) =>
        NodeAsync(model, node, nodeType, where, resolve, allowKey: true);

    /// <summary>
    /// What <c>composition set</c> changes on a node: <c>name</c>, <c>displayTemplate</c>, <c>displaySettings</c> (a null
    /// value removes one) and <c>properties</c>.
    /// </summary>
    public static CompositionNodeValue Change(CmsModel model, JsonObject value, string where)
    {
        Unknown(value, ChangeFields, where);
        return new CompositionNodeValue
        {
            Name = Text(value["name"], $"{where}.name"),
            DisplayTemplate = Text(value["displayTemplate"], $"{where}.displayTemplate"),
            DisplaySettings = Settings(value["displaySettings"], $"{where}.displaySettings"),
            Properties = value["properties"] is null ? null
                : value["properties"] is JsonObject properties ? Values(model, null, properties, $"{where}.properties")
                : throw new UsageException($"{where}.properties must be an object of property names to values."),
        };
    }

    private static readonly HashSet<string> ChangeFields = new(StringComparer.Ordinal) { "name", "displayTemplate", "displaySettings", "properties" };

    private static async Task<CompositionNodeValue> NodeAsync(CmsModel model, JsonObject node, string? nodeType, string where, Resolvers resolve, bool allowKey)
    {
        Unknown(node, NodeFields, where);
        if (node["missing"] is not null)
        {
            throw new UsageException($"{where} is a node whose block isn't stored (missing); it can't be written back.", "Leave it out.");
        }
        var type = Text(node["type"], $"{where}.type");
        var typeId = type is null ? (int?)null : model.RequireType(type).Id;
        var reference = Text(node["ref"], $"{where}.ref")
            ?? (node["content"] is JsonObject content ? Text(content["ref"], $"{where}.content.ref") ?? Text(content["guid"], $"{where}.content.guid") : null);
        var given = Text(node["nodeType"], $"{where}.nodeType");
        if (given is not null && nodeType is not null && Normal(given) != Normal(nodeType) && !(Normal(nodeType) == "section" && Normal(given) == "component"))
        {
            throw new UsageException($"{where} is a {given}, where a {nodeType} goes.");
        }
        var blueprint = Text(node["blueprint"], $"{where}.blueprint");
        return new CompositionNodeValue
        {
            NodeType = (given ?? nodeType) is { } shown ? Normal(shown) : null,
            Key = allowKey ? Text(node["key"], $"{where}.key") : null,
            Name = Text(node["name"], $"{where}.name"),
            // A shared block's type is its own: only checked against an inline one.
            Type = reference is null ? type : null,
            Ref = reference is null ? null : await resolve.Ref(reference),
            Blueprint = blueprint is null ? null : await resolve.Blueprint(blueprint),
            DisplayTemplate = Text(node["displayTemplate"], $"{where}.displayTemplate"),
            DisplaySettings = Settings(node["displaySettings"], $"{where}.displaySettings"),
            Properties = node["properties"] is null ? null
                : node["properties"] is JsonObject properties ? Values(model, typeId, properties, $"{where}.properties", keyed: allowKey && node["key"] is not null)
                : throw new UsageException($"{where}.properties must be an object of property names to values."),
            Nodes = await ChildrenAsync(model, node, where, resolve),
        };
    }

    private static async Task<IReadOnlyList<CompositionNodeValue>?> ChildrenAsync(CmsModel model, JsonObject node, string where, Resolvers resolve)
    {
        var lists = Lists.Where(l => node[l] is not null).ToList();
        if (lists.Count > 1)
        {
            throw new UsageException($"{where} has {string.Join(" and ", lists)}; a node's children go in one list.");
        }
        if (lists.Count == 0)
        {
            return null;
        }
        var list = lists[0];
        if (node[list] is not JsonArray items)
        {
            throw new UsageException($"{where}.{list} must be an array.");
        }
        var nodeType = list switch
        {
            "sections" => "section",
            "rows" => "row",
            "columns" => "column",
            "elements" => "component",
            _ => null,
        };
        var children = new List<CompositionNodeValue>();
        for (var i = 0; i < items.Count; i++)
        {
            var itemWhere = $"{where}.{list}[{i}]";
            children.Add(items[i] is JsonObject child
                ? await NodeAsync(model, child, nodeType, itemWhere, resolve, allowKey: true)
                : throw new UsageException($"{itemWhere} must be an object (a node)."));
        }
        return children;
    }

    /// <summary>
    /// An inline block's property values as <c>set</c> takes them: <c>get</c>'s <c>{type, value}</c> unwrapped, a reference
    /// as its ref, a link without the content <c>get</c> adds, ContentArea items as <see cref="PropertyNameCheck.AreaItems"/>
    /// makes them. Names are checked against the block type when it is known.
    /// </summary>
    /// <param name="keyed">The node has a key, so it keeps its block (the type it is checked against may not be the block's).</param>
    private static IReadOnlyDictionary<string, JsonElement>? Values(CmsModel model, int? typeId, JsonObject? properties, string where, bool keyed = false)
    {
        if (properties is null)
        {
            return null;
        }
        var plain = PropertyNameCheck.PlainValues(model, typeId, properties, $"{where}.");
        if (typeId is { } id)
        {
            try
            {
                plain = PropertyNameCheck.Prepare(model, id, plain)!;
            }
            catch (UsageException ex)
            {
                // Say which node, and that a keyed node's type can't change (its block stays).
                var node = where.EndsWith(".properties", StringComparison.Ordinal) ? where[..^".properties".Length] : where;
                var type = model.TypeName(id);
                throw new UsageException($"{node}: {ex.Message}", keyed
                    ? $"This node has a key, so it keeps its block: if that isn't {A(type)}, leave key out to put a new {type} there, or leave type out. {ex.Hint}"
                    : ex.Hint);
            }
        }
        return PropertyArguments.ToRequest(plain);
    }

    private static string A(string noun) => (noun.Length > 0 && "aeiouAEIOU".Contains(noun[0]) ? "an " : "a ") + noun;

    private static void Unknown(JsonObject node, HashSet<string> allowed, string where)
    {
        if (node.Select(p => p.Key).FirstOrDefault(k => !allowed.Contains(k)) is { } unknown)
        {
            throw new UsageException($"{where}: unknown field \"{unknown}\" (allowed: {string.Join(", ", allowed)}).", Hint);
        }
    }

    private static string? Text(JsonNode? node, string where) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<Guid>(out var guid) => guid.ToString(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
        _ => throw new UsageException($"{where} must be a string."),
    };

    private static IReadOnlyDictionary<string, string?>? Settings(JsonNode? node, string where)
    {
        if (node is null)
        {
            return null;
        }
        if (node is not JsonObject settings)
        {
            throw new UsageException($"{where} must be an object of setting keys to values, e.g. {{\"color\": \"accent\"}}.");
        }
        return settings.ToDictionary(s => s.Key, s => s.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False => value.GetValueKind() == JsonValueKind.True ? "true" : "false",
            _ => throw new UsageException($"{where}.{s.Key} must be a string."),
        }, StringComparer.Ordinal);
    }

    /// <summary><c>element</c> (and <c>elements</c>' entries) is the CMS's <c>component</c>.</summary>
    private static string Normal(string nodeType) => nodeType.Trim().ToLowerInvariant() switch
    {
        "element" or "component" => "component",
        var other => other,
    };
}
