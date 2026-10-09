using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Protocol;

namespace OptiCli.Integration.Comparison;

/// <summary>
/// Reduces both sides to one comparable form: properties keyed by name, each <c>{type, value, blockType?}</c>,
/// where references are refs, ContentArea items are <c>{ref, displayOption, ...}</c>, links are
/// <c>{href, text, title, target}</c>, and empty values (null, "", [], {}) are dropped at every level.
/// </summary>
/// <remarks>
/// Only presentation is reduced here: the DB side's resolved names, URLs and link lists are derived from
/// the refs that are compared, and "culture"/"truncated" are output annotations (the DB side runs with
/// <c>--full</c>). Anything that changes meaning must show up as a difference.
/// </remarks>
internal static class Canonical
{
    // ---- DB side: the JsonObject `get --full --all-properties` produces ----

    public static JsonObject FromDb(JsonObject properties)
    {
        var result = new JsonObject();
        foreach (var (name, node) in properties)
        {
            if (node is not JsonObject property || property["type"]?.GetValue<string>() is not { } type)
            {
                continue;
            }
            var value = property["value"];
            var canonical = type switch
            {
                "Block" => value is JsonObject nested ? FromDb(nested) : null,
                "BlockList" => value is JsonArray items ? Array(items.Select(i => (JsonNode?)(i is JsonObject o ? FromDb(o) : new JsonObject()))) : null,
                "ContentArea" => value is JsonArray items ? Array(items.Select(AreaItemFromDb)) : null,
                // A list of references (IList<ContentReference>) keeps the type of its items.
                "ContentReference" or "PageReference" => value is JsonArray list ? Array(list.Select(RefOf)) : RefOf(value),
                "ContentReferenceList" => value is JsonArray items ? Array(items.Select(i => i is JsonObject ? RefOf(i) : i?.DeepClone())) : null,
                "LinkCollection" => value is JsonArray items ? Array(items.Select(Link)) : null,
                "LinkItem" => value is JsonObject ? Link(value) : value?.DeepClone(),
                "Date" => Date(value),
                _ => Dates(value?.DeepClone()),
            };
            Add(result, name, type, canonical, property["blockType"]?.GetValue<string>());
        }
        return result;
    }

    private static JsonNode? AreaItemFromDb(JsonNode? node)
    {
        if (node is not JsonObject item)
        {
            return null;
        }
        var result = new JsonObject();
        if (item["inline"]?.GetValue<bool>() == true)
        {
            result["inline"] = true;
            result["type"] = item["type"]?.DeepClone();
            result["name"] = item["name"]?.DeepClone();
            result["properties"] = item["properties"] is JsonObject properties ? FromDb(properties) : null;
        }
        else
        {
            result["ref"] = RefOf(item);
        }
        result["displayOption"] = item["displayOption"]?.DeepClone();
        result["group"] = item["group"]?.DeepClone();
        result["visitorGroups"] = item["visitorGroups"]?.DeepClone();
        return Prune(result);
    }

    /// <summary>An identity's ref; for an unresolvable GUID, <c>guid:...</c>.</summary>
    private static JsonNode? RefOf(JsonNode? identity) => identity switch
    {
        JsonObject o when o["ref"]?.GetValue<string>() is { } reference => reference,
        JsonObject o when o["guid"]?.GetValue<string>() is { } guid => $"guid:{guid}",
        _ => null,
    };

    private static JsonNode? Link(JsonNode? node) => node is JsonObject link
        ? Prune(new JsonObject
        {
            ["href"] = link["href"]?.DeepClone(),
            ["text"] = link["text"]?.DeepClone(),
            ["title"] = link["title"]?.DeepClone(),
            ["target"] = link["target"]?.DeepClone(),
        })
        : null;

    // ---- Agent side: ContentItem.Properties ----

    public static JsonObject FromAgent(IReadOnlyDictionary<string, ContentItemProperty> properties)
    {
        var result = new JsonObject();
        foreach (var (name, property) in properties)
        {
            var value = property.Value is { } element ? JsonNode.Parse(element.GetRawText()) : null;
            var canonical = property.Type switch
            {
                "Block" => property.Value is { } block ? FromAgent(Nested(block)) : null,
                "BlockList" => property.Value is { ValueKind: JsonValueKind.Array } list
                    ? Array(list.EnumerateArray().Select(i => (JsonNode?)FromAgent(Nested(i))))
                    : null,
                "ContentArea" => property.Value is { } area
                    ? Array(area.Deserialize<List<ContentItemAreaEntry>>(AgentJson.Options)!.Select(AreaItemFromAgent))
                    : null,
                "LinkCollection" => value is JsonArray items ? Array(items.Select(Link)) : null,
                "LinkItem" => value is JsonObject ? Link(value) : value,
                "Date" => Date(value),
                _ => Dates(value),
            };
            Add(result, name, property.Type, canonical, property.BlockType);
        }
        return result;
    }

    private static JsonNode? AreaItemFromAgent(ContentItemAreaEntry entry) => Prune(new JsonObject
    {
        ["ref"] = entry.Ref ?? (entry.Guid is { } guid ? $"guid:{guid}" : null),
        ["inline"] = entry.Inline,
        ["type"] = entry.Type,
        ["name"] = entry.Name,
        ["properties"] = entry.Properties is { } properties ? FromAgent(properties) : null,
        ["displayOption"] = entry.DisplayOption,
        ["group"] = entry.Group,
        ["visitorGroups"] = entry.VisitorGroups is { } groups ? new JsonArray(groups.Select(g => (JsonNode?)g).ToArray()) : null,
    });

    private static Dictionary<string, ContentItemProperty> Nested(JsonElement element) =>
        element.Deserialize<Dictionary<string, ContentItemProperty>>(AgentJson.Options) ?? [];

    // ---- Visual Builder compositions (CMS 13) ----

    /// <summary>
    /// The DB side's composition (<c>get</c>'s <c>composition</c>) as a tree of <c>{nodeType, key, name, type,
    /// displayTemplate, displaySettings, ref, properties, nodes}</c>, as the CMS's mapper has it. Nodes whose binding found
    /// nothing (<c>missing</c>) and items no node binds (<c>unplaced</c>) are left out: the mapper drops them. The root's key,
    /// name and type are the content's own, compared as identity fields.
    /// </summary>
    public static JsonNode? CompositionFromDb(JsonObject? composition) => composition is null ? null : Prune(new JsonObject
    {
        ["displayTemplate"] = composition["displayTemplate"]?.DeepClone(),
        ["displaySettings"] = composition["displaySettings"]?.DeepClone(),
        ["nodes"] = CompositionChildren(composition),
    });

    private static JsonArray CompositionChildren(JsonObject entry)
    {
        var nodes = new JsonArray();
        foreach (var (list, type) in new[] { ("sections", (string?)null), ("rows", "row"), ("columns", "column"), ("elements", "component"), ("nodes", null) })
        {
            foreach (var child in entry[list] as JsonArray ?? [])
            {
                if (child is not JsonObject node || node["missing"] is not null)
                {
                    continue;
                }
                // A bound node with a grid of its own is a section, any other bound node a component (as the mapper reads it).
                var nodeType = type ?? node["nodeType"]?.GetValue<string>()
                    ?? (node.ContainsKey("rows") || node.ContainsKey("columns") || node.ContainsKey("elements") || node.ContainsKey("nodes") ? "section" : "component");
                if (list == "nodes" && nodeType is "section" or "component")
                {
                    nodeType = node.ContainsKey("rows") || node.ContainsKey("columns") || node.ContainsKey("elements") || node.ContainsKey("nodes") ? "section" : "component";
                }
                nodes.Add(Prune(new JsonObject
                {
                    ["nodeType"] = nodeType,
                    ["key"] = node["key"]?.DeepClone(),
                    ["name"] = node["name"]?.DeepClone(),
                    ["type"] = node["type"]?.DeepClone(),
                    ["displayTemplate"] = node["displayTemplate"]?.DeepClone(),
                    ["displaySettings"] = node["displaySettings"]?.DeepClone(),
                    ["ref"] = node["content"] is JsonObject content ? RefOf(content) : null,
                    ["properties"] = node["properties"] is JsonObject properties ? FromDb(properties) : null,
                    ["nodes"] = CompositionChildren(node),
                }) ?? new JsonObject());
            }
        }
        return nodes;
    }

    /// <summary>The CMS's composition (<see cref="ContentItem.Composition"/>) in the shape of <see cref="CompositionFromDb"/>.</summary>
    public static JsonNode? CompositionFromAgent(ContentItemCompositionNode? root) => root is null ? null : Prune(new JsonObject
    {
        ["displayTemplate"] = root.DisplayTemplate,
        ["displaySettings"] = Settings(root.DisplaySettings),
        ["nodes"] = new JsonArray((root.Nodes ?? []).Select(CompositionNodeFromAgent).ToArray()),
    });

    private static JsonNode? CompositionNodeFromAgent(ContentItemCompositionNode node) => Prune(new JsonObject
    {
        ["nodeType"] = node.NodeType,
        ["key"] = node.Key,
        ["name"] = node.Name,
        ["type"] = node.Type,
        ["displayTemplate"] = node.DisplayTemplate,
        ["displaySettings"] = Settings(node.DisplaySettings),
        ["ref"] = node.Ref,
        ["properties"] = node.Properties is { } properties ? FromAgent(properties) : null,
        ["nodes"] = new JsonArray((node.Nodes ?? []).Select(CompositionNodeFromAgent).ToArray()),
    }) ?? new JsonObject();

    private static JsonObject? Settings(IReadOnlyDictionary<string, string>? settings) =>
        settings is { Count: > 0 } ? new JsonObject(settings.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value))) : null;

    // ---- Shared ----

    /// <summary>
    /// Dates as UTC to the second, when the text says which zone it is in. The DB stores UTC; a value without an
    /// offset is left as it is, so a side that forgets to say "UTC" shows up as a difference.
    /// </summary>
    public static JsonNode? Date(JsonNode? value)
    {
        if (value is not JsonValue text || !text.TryGetValue<string>(out var raw))
        {
            return value?.DeepClone();
        }
        return HasZone(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : raw;
    }

    /// <summary>
    /// Date-times inside other values (custom property JSON) as UTC: the DB has them as stored, the CMS as it
    /// serialises the loaded objects, in the server's zone.
    /// </summary>
    private static JsonNode? Dates(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length >= 20 && char.IsAsciiDigit(text[0]) && text[10] == 'T' && HasZone(text):
                return Date(value);
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    obj[key] = Dates(obj[key]?.DeepClone());
                }
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Dates(array[i]?.DeepClone());
                }
                return array;
            default:
                return node;
        }
    }

    private static bool HasZone(string value) =>
        value.EndsWith('Z') || (value.Length > 6 && value[^6] is '+' or '-' && value[^3] == ':');

    private static void Add(JsonObject result, string name, string type, JsonNode? value, string? blockType)
    {
        if (Prune(value) is not { } kept)
        {
            return;
        }
        var property = new JsonObject { ["type"] = type, ["value"] = kept };
        if (blockType is not null)
        {
            property["blockType"] = blockType;
        }
        result[name] = property;
    }

    private static JsonArray? Array(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item?.DeepClone());
        }
        return array;
    }

    /// <summary>
    /// Drops empty values from objects, recursively; null when nothing is left. Array positions are kept (an empty
    /// block list item is still an item), only their contents are pruned.
    /// </summary>
    public static JsonNode? Prune(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                return value.TryGetValue<string>(out var text) && text.Length == 0 ? null : value;
            case JsonArray array:
                if (array.Count == 0)
                {
                    return null;
                }
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Prune(item?.DeepClone()) ?? (item is JsonObject ? new JsonObject() : null));
                }
                return items;
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, child) in obj)
                {
                    if (Prune(child?.DeepClone()) is { } kept)
                    {
                        result[key] = kept;
                    }
                }
                return result.Count == 0 ? null : result;
            default:
                return node;
        }
    }

    /// <summary>JSON with object keys sorted, so equal values have equal text.</summary>
    public static string Text(JsonNode? node)
    {
        return Sorted(node)?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";

        static JsonNode? Sorted(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Sorted).ToArray()),
            _ => node?.DeepClone(),
        };
    }
}
