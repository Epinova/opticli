using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Text;

namespace OptiCli.Core.Writes;

/// <summary>
/// Checks property names against the content model in the database before anything is sent to the
/// site, so a typo fails fast with "did you mean" instead of a round trip.
/// </summary>
public static class PropertyNameCheck
{
    /// <summary>
    /// Names the agent accepts that are not property definitions: the content name, the writable built-in metadata and
    /// the publish dates (<c>StartPublish</c>, <c>StopPublish</c>), a page's sorting (<c>ChildSortOrder</c>,
    /// <c>SortIndex</c>), shortcut and simple address, and the built-in category of pages and media, most also under
    /// their metadata names.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltIn =
    [
        "Name", "PageURLSegment", "PageVisibleInMenu", "StartPublish", "StopPublish", "PageStartPublish", "PageStopPublish",
        "ChildSortOrder", "SortIndex", "PageChildOrderRule", "PagePeerOrder",
        "Shortcut", "SimpleAddress", "ExternalURL", "PageExternalURL", "Category", "PageCategory",
    ];

    /// <exception cref="UsageException">A name is not a property of the type (or of the local block it is nested in).</exception>
    public static void Check(CmsModel model, int contentTypeId, JsonObject? properties) => Prepare(model, contentTypeId, properties);

    /// <summary>
    /// <see cref="Check"/>, and the values as the agent takes them: ContentArea items as <see cref="AreaItems"/> makes them,
    /// an inline block's values by position (<c>MainArea[2]</c>) without <c>get</c>'s <c>{type, value}</c>.
    /// </summary>
    /// <returns>A copy of <paramref name="properties"/>; null for null.</returns>
    /// <exception cref="UsageException">As <see cref="Check"/>, and for ContentArea items that aren't one kind of item or the other.</exception>
    public static JsonObject? Prepare(CmsModel model, int contentTypeId, JsonObject? properties)
    {
        if (properties is null)
        {
            return null;
        }
        var copy = (JsonObject)properties.DeepClone();
        Check(model, contentTypeId, copy, prefix: "", topLevel: true);
        return copy;
    }

    /// <summary>What <c>get</c> shows of a shared block in a ContentArea that <c>set</c> doesn't take (it names the content).</summary>
    private static readonly string[] SharedItemDecoration =
        ["type", "name", "language", "status", "url", "kind", "missing", "deleted", "blueprint", "provider", "visitorGroupNames", "renderSettings"];

    /// <summary>The fields of an inline block item <c>set</c> takes; <c>get</c>'s <c>renderSettings</c> and <c>visitorGroupNames</c> are left out.</summary>
    private static readonly string[] InlineItemFields = ["type", "name", "properties", "displayOption", "group", "visitorGroups"];

    /// <summary>
    /// A ContentArea's items as the agent takes them, from <c>get</c>'s shape or <c>set</c>'s: a shared block by
    /// <c>ref</c> (or <c>guid</c>), what <c>get</c> adds about the content left out; an inline block by <c>type</c> (a
    /// block type), <c>name</c> and <c>properties</c> (names checked, <c>get</c>'s <c>{type, value}</c> unwrapped), also
    /// as <c>get</c> shows it (<c>inline: true</c>). Display option and personalization as given.
    /// </summary>
    /// <exception cref="UsageException">An item that gives both kinds, or neither; an unknown type, or one that isn't a block type.</exception>
    public static JsonArray AreaItems(CmsModel model, JsonArray items, string where)
    {
        var result = new JsonArray();
        for (var i = 0; i < items.Count; i++)
        {
            var itemWhere = $"{where}[{i}]";
            if (items[i] is not JsonObject item)
            {
                throw new UsageException($"'{itemWhere}' must be an object: {{\"ref\": \"123\"}} for a shared block, {{\"type\": \"TeaserBlock\", \"properties\": {{...}}}} for an inline one.");
            }
            var shared = item["ref"] is not null || item["guid"] is not null;
            var flagged = item["inline"] is JsonValue flag && flag.GetValueKind() == JsonValueKind.True;
            if (shared && (flagged || item["properties"] is not null))
            {
                throw new UsageException($"'{itemWhere}' gives both a shared block (ref or guid) and an inline block (inline, properties).",
                    "An item is one or the other: {\"ref\": \"123\"} for a shared block, {\"type\": \"TeaserBlock\", \"properties\": {...}} for an inline one.");
            }
            var copy = (JsonObject)item.DeepClone();
            if (shared)
            {
                foreach (var decoration in SharedItemDecoration)
                {
                    copy.Remove(decoration);
                }
                result.Add(copy);
                continue;
            }
            if (!flagged && item["type"] is null && item["properties"] is null)
            {
                throw new UsageException($"'{itemWhere}' names no block.",
                    "Give {\"ref\": \"123\"} (or guid) for a shared block, or {\"type\": \"TeaserBlock\", \"properties\": {...}} for an inline one.");
            }
            var typeName = item["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var text) ? text
                : item["type"] is JsonValue number && number.GetValueKind() == JsonValueKind.Number ? number.ToJsonString()
                : throw new UsageException($"'{itemWhere}' is an inline block without its type.", "Give its block type: {\"type\": \"TeaserBlock\", \"properties\": {...}}.");
            var type = BlockType(model, typeName, itemWhere);
            var inline = new JsonObject { ["type"] = type.Name };
            foreach (var field in InlineItemFields.Skip(1))
            {
                if (copy[field] is { } value)
                {
                    copy.Remove(field);
                    inline[field] = value;
                }
            }
            if (inline["properties"] is { } properties)
            {
                if (properties is not JsonObject values)
                {
                    throw new UsageException($"'{itemWhere}.properties' must be an object of {type.Name} property names to values.");
                }
                inline["properties"] = InlineValues(model, type, values, $"{itemWhere}.");
            }
            result.Add(inline);
        }
        return result;
    }

    /// <summary>
    /// An inline block's values as the agent takes them: <c>get</c>'s <c>{type, value}</c> unwrapped (a reference as its
    /// ref), names checked against <paramref name="type"/>.
    /// </summary>
    /// <param name="prefix">What the names are shown after in messages (<c>MainArea[2].</c>).</param>
    public static JsonObject InlineValues(CmsModel model, ContentTypeInfo type, JsonObject values, string prefix)
    {
        var plain = new JsonObject();
        foreach (var (name, value) in values)
        {
            plain[name] = GetShape.IsWrapped(value) ? GetShape.Unwrap((JsonObject)value!, $"{prefix}{name}") : GetShape.Plain(value);
        }
        Check(model, type.Id, plain, prefix, topLevel: false);
        return plain;
    }

    /// <summary>
    /// The values of an inline block whose type the CLI doesn't know (the block at a position of the content, which the site
    /// looks up), as the agent takes them: <c>get</c>'s <c>{type, value}</c> unwrapped, and the ContentAreas among them, as
    /// <c>get</c> shows them or under a name that is a ContentArea in every type that has it, as <see cref="AreaItems"/>
    /// makes them; local blocks likewise. The names are left to the site.
    /// </summary>
    /// <param name="prefix">What the names are shown after in messages (<c>MainArea[2].</c>).</param>
    public static JsonObject UntypedValues(CmsModel model, JsonObject values, string prefix)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            var where = $"{prefix}{name}";
            var wrapped = GetShape.IsWrapped(value);
            var type = wrapped ? (string?)value!["type"] : null;
            if (AreaItemPath.Parse(name) is not null)
            {
                result[name] = value is JsonObject nested ? UntypedValues(model, nested, $"{where}.") : value?.DeepClone();
            }
            else if (type == "ContentArea" || (!wrapped && value is JsonArray && IsAreaName(model, name)))
            {
                var items = wrapped ? value!["value"] : value;
                result[name] = items is JsonArray array ? AreaItems(model, array, where) : items?.DeepClone();
            }
            else if (type == "Block" && value!["value"] is JsonObject block)
            {
                result[name] = UntypedValues(model, block, $"{where}.");
            }
            else if (wrapped)
            {
                result[name] = GetShape.Unwrap((JsonObject)value!, where);
            }
            else if (value is JsonObject local && model.Properties.Values.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.BlockType is not null && !p.IsList))
            {
                result[name] = UntypedValues(model, local, $"{where}.");
            }
            else
            {
                result[name] = value?.DeepClone();
            }
        }
        return result;
    }

    /// <summary>Whether every property of this name, in any type, is a ContentArea.</summary>
    private static bool IsAreaName(CmsModel model, string name)
    {
        var definitions = model.Properties.Values.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return definitions.Count > 0 && definitions.All(p => p.TypeName == "ContentArea");
    }

    /// <summary>A block type by name, class name, GUID or id, for an inline block.</summary>
    /// <exception cref="UsageException">No such type, or not a block type.</exception>
    public static ContentTypeInfo BlockType(CmsModel model, string name, string where)
    {
        ContentTypeInfo type;
        try
        {
            type = int.TryParse(name.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                ? model.Types.FirstOrDefault(t => t.Id == id) ?? throw new NotFoundException($"No content type has the id {id}.")
                : model.RequireType(name);
        }
        catch (NotFoundException ex)
        {
            throw new UsageException($"{where}: {ex.Message}", ex.Hint);
        }
        return type.Kind is ContentKind.Block or ContentKind.Section or ContentKind.Element
            ? type
            : throw new UsageException($"{where}: {type.Name} is a {type.Kind.ToString().ToLowerInvariant()} type, not a block type, so it can't be an inline block.",
                "`opticli types --kind block` lists the block types; `opticli allowed-in <type>` says which areas take one.");
    }

    /// <summary>The ContentArea property <paramref name="name"/> of the type, with its exact name.</summary>
    /// <exception cref="UsageException">No such property, or it is not a ContentArea.</exception>
    public static PropertyDefinition RequireContentArea(CmsModel model, int contentTypeId, string name)
    {
        var definition = Find(model, contentTypeId, name, "", topLevel: false);
        return definition!.TypeName == "ContentArea"
            ? definition
            : throw new UsageException(
                $"'{definition.Name}' is a {definition.TypeName} property, not a ContentArea.",
                Hint("ContentArea properties", model.PropertiesOf(contentTypeId).Where(p => p.TypeName == "ContentArea").Select(p => p.Name), name));
    }

    /// <summary>
    /// The ContentArea an area edit names, with its exact name: a property of the type, or a path to one inside a block
    /// (<c>MainArea[0].Area</c>: the inline block at position 0; <c>Hero.Area</c>: a local block). Names are checked as far
    /// as the type is known: an inline block's type is in the content, so the site checks what follows it.
    /// </summary>
    /// <exception cref="UsageException">No such property, not a ContentArea, or a path that doesn't lead to one.</exception>
    public static string AreaPath(CmsModel model, int contentTypeId, string path)
    {
        if (!path.Contains('.') && !path.Contains('['))
        {
            return RequireContentArea(model, contentTypeId, path).Name;
        }
        var segments = path.Split('.');
        int? typeId = contentTypeId;
        var result = new List<string>();
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i].Trim();
            var position = AreaItemPath.Parse(segment);
            if (position is null && segment.Contains('['))
            {
                throw new UsageException($"'{segment}' in '{path}' is neither a property name nor an item of a ContentArea, like MainArea[2].");
            }
            var name = position?.Property ?? segment;
            var last = i == segments.Length - 1;
            if (typeId is { } type)
            {
                var definition = Find(model, type, name, "", topLevel: false)!;
                name = definition.Name;
                if (last ? position is not null || definition.TypeName != "ContentArea" : position is not null && definition.TypeName != "ContentArea")
                {
                    throw new UsageException($"'{path}' doesn't name a ContentArea: '{definition.Name}' is a {definition.TypeName} property{(last && position is not null ? ", and the path ends at an item" : "")}.",
                        "Name the area: MainArea, or one inside a block, e.g. MainArea[0].Area (the inline block at position 0) or Hero.Area (a local block).");
                }
                if (!last && position is null && definition is not { BlockType: not null, IsList: false })
                {
                    throw new UsageException($"'{path}' doesn't lead to a ContentArea: '{definition.Name}' is neither a local block nor a ContentArea item.");
                }
                typeId = position is null ? definition.BlockType : null;
            }
            else if (last && position is not null)
            {
                throw new UsageException($"'{path}' ends at an item of a ContentArea, not at a ContentArea.");
            }
            result.Add(position is null ? name : $"{name}[{position.Value.Index}]");
        }
        return string.Join('.', result);
    }

    private static void Check(CmsModel model, int contentTypeId, JsonObject properties, string prefix, bool topLevel)
    {
        foreach (var (name, value) in properties.ToList())
        {
            if (AreaItemPath.Parse(name) is { } position)
            {
                var area = Find(model, contentTypeId, position.Property, prefix, topLevel: false)!;
                if (area.TypeName != "ContentArea")
                {
                    throw new UsageException($"'{prefix}{name}' names an item of a ContentArea, and '{area.Name}' is a {area.TypeName} property.",
                        "Positions in brackets are for a ContentArea's inline blocks; set a block list as a whole array.");
                }
                if (value is not JsonObject values)
                {
                    throw new UsageException($"'{prefix}{name}' takes the inline block's values: {area.Name}[{position.Index}].Heading=Hi, or an object of its property names to values.",
                        $"To replace or remove the item, set {area.Name} whole, or use `opticli area`.");
                }
                // The names are the inline block's, whose type is in the content: the site checks them.
                properties[name] = UntypedValues(model, values, $"{prefix}{name}.");
                continue;
            }
            var definition = Find(model, contentTypeId, name, prefix, topLevel);
            if (value is JsonArray areaItems && definition?.TypeName == "ContentArea")
            {
                properties[name] = AreaItems(model, areaItems, $"{prefix}{definition.Name}");
            }
            else if (value is JsonObject nested && definition?.BlockType is { } blockType)
            {
                Check(model, blockType, nested, $"{prefix}{definition.Name}.", topLevel: false);
            }
            else if (value is JsonArray items && definition is { IsList: true, BlockType: { } itemType })
            {
                // A block list: every item is an object of the block type's properties.
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is not JsonObject item)
                    {
                        throw new UsageException(
                            $"'{prefix}{definition.Name}[{i}]' must be an object of {model.TypeName(itemType)} property names to values.",
                            $"A block list is an array of objects, e.g. [{{\"Name\": \"...\"}}]; see `opticli type {model.TypeName(itemType)}`.");
                    }
                    Check(model, itemType, item, $"{prefix}{definition.Name}[{i}].", topLevel: false);
                }
            }
        }
    }

    /// <returns>Null for a built-in name.</returns>
    private static PropertyDefinition? Find(CmsModel model, int contentTypeId, string name, string prefix, bool topLevel)
    {
        var definitions = model.PropertiesOf(contentTypeId).ToList();
        var match = definitions.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            // CMS 13: the properties an experience's or section's composition is stored in are written as the composition.
            return !Properties.Compositions.StorageProperties(model, contentTypeId).Contains(match.Name)
                ? match
                : throw new UsageException(
                    $"'{prefix}{match.Name}' is where {model.TypeName(contentTypeId)} stores its Visual Builder composition; it isn't set directly.",
                    "Change the composition with `opticli composition <ref> add|remove|move|set`, or write it whole as composition in --values (as get shows it).");
        }
        if (topLevel && BuiltIn.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var owner = model.TypeName(contentTypeId);
        throw new UsageException(
            $"'{prefix}{name}' is not a property of {owner}.",
            Hint($"{owner} properties", definitions.Select(p => p.Name), name));
    }

    private static string Hint(string what, IEnumerable<string> names, string input)
    {
        var list = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var suggestion = Suggestions.DidYouMean(input, list);
        var all = list.Count == 0 ? $"{what}: none." : $"{what}: {string.Join(", ", list)}.";
        return suggestion is null ? all : $"{suggestion} {all}";
    }
}
