using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;

namespace OptiCli.Core.Properties;

/// <summary>
/// CMS 13's Visual Builder: turns the two properties an experience (or a section) stores its composition in into the
/// composition itself, as the CMS's <c>ICompositionMapper</c> reads it.
/// </summary>
/// <remarks>
/// <para>Storage (phase 0's findings): <c>Layout</c> (property type <c>Layout</c>) holds JSON, an outline of section nodes
/// for an experience, a grid of rows → columns → component nodes for a section. A section or component node binds to an
/// item of the ContentArea <c>UnstructuredData</c> by key (<c>"propertyBinding": "UnstructuredData[&lt;key&gt;]"</c>, the
/// item's <c>data-epi-block-id</c>), and the item's <c>data-inlineblockname</c> is the node's name. Items are inline blocks
/// (their values are scoped rows of the content) or shared blocks used by reference. An inline section has its own
/// <c>Layout</c> and <c>UnstructuredData</c>, one level down. Rows and columns exist only in the JSON (with an <c>id</c>).</para>
/// <para>As the mapper does: a bound block that has a layout itself (a section) is a section with its own grid and display
/// template; any other bound block is a component (an element), whose display template and settings are on its layout
/// node. Unlike the mapper, a node whose binding finds nothing is kept (<c>missing: true</c>), and items no node binds are
/// listed as <c>unplaced</c>, so nothing stored is hidden.</para>
/// </remarks>
public static class Compositions
{
    /// <summary>The property type of the layout (<c>tblPropertyDefinitionType.Name</c>).</summary>
    public const string LayoutType = "Layout";

    /// <summary>The ContentArea items bind to unless a binding names another property.</summary>
    public const string ItemsProperty = "UnstructuredData";

    /// <summary>The <c>get</c> field (and <c>--fields</c> name) the composition is shown in.</summary>
    public const string Field = "composition";

    private const string KeyAttribute = "epi-block-id";

    /// <summary>The layout property of a content type; null when the type has no composition.</summary>
    public static PropertyDefinition? LayoutProperty(CmsModel model, int? typeId) =>
        typeId is { } id ? model.PropertiesOf(id).FirstOrDefault(p => p.TypeName == LayoutType) : null;

    /// <summary>Whether content of the type stores a Visual Builder composition (experiences and sections).</summary>
    public static bool IsLayouted(CmsModel model, int? typeId) => LayoutProperty(model, typeId) is not null;

    /// <summary>
    /// The names of the properties a composition is stored in (the layout and the ContentAreas it binds to), which the
    /// composition replaces in the output.
    /// </summary>
    public static IReadOnlyList<string> StorageProperties(CmsModel model, int typeId) =>
        LayoutProperty(model, typeId) is { } layout
            ? [layout.Name, .. model.PropertiesOf(typeId).Where(p => p.Name.Equals(ItemsProperty, StringComparison.OrdinalIgnoreCase)).Select(p => p.Name)]
            : [];

    /// <summary>
    /// The composition of content of <paramref name="typeId"/>, from its stored rows (<paramref name="nodes"/>) and its
    /// decoded properties (<paramref name="decoded"/>, as <see cref="PropertyDecoder"/> makes them), from which the layout and
    /// the ContentAreas it binds to are removed. Null, and nothing removed, when the type has no layout property or its
    /// layout isn't readable JSON.
    /// </summary>
    /// <param name="kind">The content's kind: a section's empty composition is a grid, anything else's an outline.</param>
    public static JsonObject? Extract(CmsModel model, int typeId, ContentKind kind, IReadOnlyDictionary<int, PropertyNode> nodes, JsonObject decoded) =>
        Build(model, typeId, kind == ContentKind.Section ? "grid" : "outline", nodes, decoded, root: true);

    private static JsonObject? Build(CmsModel model, int? typeId, string emptyLayout, IReadOnlyDictionary<int, PropertyNode> nodes, JsonObject decoded, bool root)
    {
        if (LayoutProperty(model, typeId) is not { } layoutProperty)
        {
            return null;
        }
        var text = nodes.GetValueOrDefault(layoutProperty.Id)?.Row is { } row ? row.LongString ?? row.String : null;
        JsonObject? layout = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                layout = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
            if (layout is null)
            {
                return null;
            }
        }

        var context = new Level(model, typeId!.Value, nodes, decoded);
        var result = new JsonObject { ["layout"] = Text(layout?["type"]) ?? emptyLayout };
        Style(result, layout);
        if (root && decoded[layoutProperty.Name]?["culture"] is JsonValue culture)
        {
            result["culture"] = culture.DeepClone();
        }
        var children = Children(context, layout?["nodes"] as JsonArray);
        Add(result, children, Text(layout?["type"]) ?? emptyLayout);
        if (context.Unplaced() is { Count: > 0 } unplaced)
        {
            result["unplaced"] = unplaced;
        }

        decoded.Remove(layoutProperty.Name);
        foreach (var area in context.Areas)
        {
            decoded.Remove(area);
        }
        return result;
    }

    /// <summary>The ContentAreas of one level (content, or an inline section), their decoded items and which were placed.</summary>
    private sealed class Level(CmsModel model, int typeId, IReadOnlyDictionary<int, PropertyNode> nodes, JsonObject decoded)
    {
        private readonly Dictionary<string, (PropertyDefinition Definition, IReadOnlyList<ContentFragment> Fragments, JsonArray Items)> _areas = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<(string Area, int Index)> _placed = [];

        public CmsModel Model { get; } = model;

        public IReadOnlyDictionary<int, PropertyNode> Nodes { get; } = nodes;

        public JsonObject Decoded { get; } = decoded;

        /// <summary>The ContentAreas items were looked up in, and <see cref="Compositions.ItemsProperty"/> always.</summary>
        public IEnumerable<string> Areas => _areas.Keys.Append(ItemsProperty).Distinct(StringComparer.OrdinalIgnoreCase);

        public (PropertyDefinition Definition, ContentFragment Fragment, JsonObject Item, PropertyNode Node)? Find(string area, string key)
        {
            if (Area(area) is not { } found)
            {
                return null;
            }
            for (var i = 0; i < found.Fragments.Count; i++)
            {
                if (found.Fragments[i].RenderSettings.TryGetValue(KeyAttribute, out var stored) && stored.Equals(key, StringComparison.OrdinalIgnoreCase)
                    && found.Items.ElementAtOrDefault(i) is JsonObject item)
                {
                    _placed.Add((found.Definition.Name, i));
                    return (found.Definition, found.Fragments[i], item, Nodes[found.Definition.Id]);
                }
            }
            return null;
        }

        /// <summary>Items of the levels' ContentAreas that no node binds.</summary>
        public JsonArray Unplaced()
        {
            var result = new JsonArray();
            if (Area(ItemsProperty) is null)
            {
                // Nothing to look at unless the items property is there.
            }
            foreach (var (name, (definition, fragments, items)) in _areas)
            {
                for (var i = 0; i < fragments.Count; i++)
                {
                    if (!_placed.Contains((name, i)) && items.ElementAtOrDefault(i) is JsonObject item)
                    {
                        result.Add(Item(this, fragments[i], item, Nodes[definition.Id].Items.GetValueOrDefault(i), null));
                    }
                }
            }
            return result;
        }

        private (PropertyDefinition Definition, IReadOnlyList<ContentFragment> Fragments, JsonArray Items)? Area(string name)
        {
            if (_areas.TryGetValue(name, out var known))
            {
                return known;
            }
            var definition = Model.PropertiesOf(typeId).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.TypeName == "ContentArea");
            if (definition is null || !Nodes.TryGetValue(definition.Id, out var node) || node.Row is not { } row)
            {
                return null;
            }
            var fragments = ContentFragmentParser.Parse(row.LongString ?? row.String);
            var items = Decoded[definition.Name]?["value"] as JsonArray ?? [];
            var area = (definition, fragments, items);
            _areas[definition.Name] = area;
            return area;
        }
    }

    /// <summary>Layout nodes as output entries, under the key their kind is listed by.</summary>
    private static List<(string Type, JsonObject Entry)> Children(Level level, JsonArray? layoutNodes)
    {
        var children = new List<(string, JsonObject)>();
        foreach (var node in layoutNodes ?? [])
        {
            if (node is not JsonObject layoutNode)
            {
                continue;
            }
            var type = Text(layoutNode["type"]) ?? "";
            children.Add(Text(layoutNode["propertyBinding"]) is { } binding
                ? Bound(level, layoutNode, binding)
                : (type, Structure(level, layoutNode, type)));
        }
        return children;
    }

    /// <summary>A row, a column, or any other node that only groups others.</summary>
    private static JsonObject Structure(Level level, JsonObject layoutNode, string type)
    {
        var entry = new JsonObject();
        Put(entry, "key", layoutNode["id"]);
        Put(entry, "name", layoutNode["name"]);
        Style(entry, layoutNode);
        Add(entry, Children(level, layoutNode["nodes"] as JsonArray), type);
        return entry;
    }

    /// <summary>A node bound to a block: a section when the block has a layout of its own, otherwise a component (element).</summary>
    private static (string Type, JsonObject Entry) Bound(Level level, JsonObject layoutNode, string binding)
    {
        var open = binding.IndexOf('[');
        var area = open > 0 ? binding[..open] : null;
        var key = open > 0 && binding.EndsWith(']') ? binding[(open + 1)..^1] : null;

        if (area is not null && key is not null && level.Find(area, key) is { } found)
        {
            var item = Item(level, found.Fragment, found.Item, found.Node.Items.GetValueOrDefault(found.Fragment.Index), layoutNode, key);
            return (item.ContainsKey("rows") || item.ContainsKey("nodes") || item.ContainsKey("columns") || item.ContainsKey("elements") || item.ContainsKey("sections") ? "section" : "component", item);
        }
        if (area is null && level.Decoded[binding] is JsonObject { } property && property["value"] is JsonObject properties)
        {
            // A binding to a block property of the content itself (the mapper's other case): the property's name is the key.
            var entry = new JsonObject { ["key"] = binding, ["name"] = binding };
            Put(entry, "type", property["blockType"]);
            Style(entry, layoutNode);
            entry["properties"] = properties.DeepClone();
            return ("component", entry);
        }
        var missing = new JsonObject();
        Put(missing, "key", key is null ? JsonValue.Create(binding) : JsonValue.Create(key));
        Put(missing, "name", layoutNode["name"]);
        Style(missing, layoutNode);
        missing["missing"] = true;
        return (Text(layoutNode["type"]) == "section" ? "section" : "component", missing);
    }

    /// <summary>One ContentArea item as a composition entry: inline (with its properties, and its own composition when it has one) or shared.</summary>
    private static JsonObject Item(Level level, ContentFragment fragment, JsonObject item, BlockItem? stored, JsonObject? layoutNode, string? key = null)
    {
        var entry = new JsonObject();
        Put(entry, "key", key is not null ? JsonValue.Create(key) : fragment.RenderSettings.TryGetValue(KeyAttribute, out var own) ? JsonValue.Create(own) : null);
        entry["name"] = fragment.InlineName ?? Text(item["name"]);
        Put(entry, "type", item["type"]);

        var inline = item["inline"] is JsonValue flag && flag.TryGetValue<bool>(out var isInline) && isInline;
        JsonObject? nested = null;
        JsonObject? properties = null;
        if (inline)
        {
            entry["inline"] = true;
            properties = item["properties"] is JsonObject decoded ? (JsonObject)decoded.DeepClone() : null;
            var typeId = fragment.InlineTypeId ?? stored?.TypeId;
            if (properties is not null && stored is not null)
            {
                nested = Build(level.Model, typeId, "grid", stored.Properties, properties, root: false);
            }
            else if (IsLayouted(level.Model, typeId))
            {
                nested = new JsonObject { ["layout"] = "grid", ["rows"] = new JsonArray() };
            }
        }
        else
        {
            var content = (JsonObject)item.DeepClone();
            foreach (var extra in new[] { "renderSettings", "displayOption", "group", "visitorGroups", "visitorGroupNames" })
            {
                content.Remove(extra);
            }
            entry["content"] = content;
        }

        if (nested is not null)
        {
            // A section: its display template and settings are in its own layout, its rows below.
            foreach (var field in new[] { "displayTemplate", "displaySettings" })
            {
                if (nested[field] is { } value)
                {
                    entry[field] = value.DeepClone();
                }
            }
        }
        else
        {
            Style(entry, layoutNode);
        }
        if (properties is { Count: > 0 })
        {
            entry["properties"] = properties;
        }
        if (nested is not null)
        {
            foreach (var (name, value) in nested.ToList())
            {
                if (name is not ("layout" or "displayTemplate" or "displaySettings"))
                {
                    nested.Remove(name);
                    entry[name] = value;
                }
            }
        }
        return entry;
    }

    /// <summary>Puts children under <c>sections</c>, <c>rows</c>, <c>columns</c> or <c>elements</c>; mixed kinds go under <c>nodes</c>, each with its <c>nodeType</c>.</summary>
    private static void Add(JsonObject entry, List<(string Type, JsonObject Entry)> children, string? layout)
    {
        var types = children.Select(c => c.Type).Distinct().ToList();
        if (children.Count == 0)
        {
            entry[layout == "grid" ? "rows" : layout == "outline" ? "sections" : "nodes"] = new JsonArray();
            return;
        }
        var single = types.Count == 1 ? Plural(types[0]) : null;
        // In an outline every bound node is a section of the experience, whether the CMS reads it as a section or as a
        // section-enabled block (a component).
        if (layout == "outline" && types.All(t => t is "section" or "component"))
        {
            single = "sections";
        }
        if (single is not null)
        {
            entry[single] = new JsonArray(children.Select(c => (JsonNode?)c.Entry).ToArray());
            return;
        }
        entry["nodes"] = new JsonArray(children.Select(c =>
        {
            var node = new JsonObject { ["nodeType"] = c.Type };
            foreach (var (name, value) in c.Entry.ToList())
            {
                c.Entry.Remove(name);
                node[name] = value;
            }
            return (JsonNode?)node;
        }).ToArray());
    }

    private static string? Plural(string type) => type switch
    {
        "section" => "sections",
        "row" => "rows",
        "column" => "columns",
        "component" => "elements",
        _ => null,
    };

    /// <summary>The node's display template and settings, when set.</summary>
    private static void Style(JsonObject entry, JsonObject? node)
    {
        if (node is null)
        {
            return;
        }
        if (Text(node["displayTemplateKey"]) is { Length: > 0 } template)
        {
            entry["displayTemplate"] = template;
        }
        if (node["displaySettings"] is JsonObject { Count: > 0 } settings)
        {
            entry["displaySettings"] = settings.DeepClone();
        }
    }

    private static void Put(JsonObject entry, string name, JsonNode? value)
    {
        if (value is not null)
        {
            entry[name] = value.DeepClone();
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
