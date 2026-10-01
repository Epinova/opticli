using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Content;
using OptiCli.Core.Output;

namespace OptiCli.Core.Properties;

/// <param name="Full">Never truncate (<c>--full</c>).</param>
/// <param name="AllProperties">Include properties without a value, as <c>value: null</c>.</param>
/// <param name="Fields">Only these top-level properties, untruncated (<c>--fields</c>); null for all.</param>
/// <param name="Expand">Inline the properties of referenced content (<c>--expand</c>), one level deep.</param>
public sealed record DecodeOptions(bool Full = false, bool AllProperties = false, IReadOnlySet<string>? Fields = null, bool Expand = false);

/// <summary>
/// Turns a <see cref="PropertyTree"/> into the output shape: an object keyed by property name, each
/// <c>{"type", "value", "culture"}</c> plus type-specific extras (resolved references, links, blocks).
/// </summary>
/// <remarks>
/// Pure: everything it needs about other content comes from <see cref="IReferenceLookup"/>, so it runs
/// once with a <see cref="ReferenceCollector"/> to learn what to load and once with the loaded identities.
/// </remarks>
public sealed class PropertyDecoder(
    CmsModel model,
    IReferenceLookup lookup,
    DecodeOptions options,
    int masterLanguageId,
    Func<int, string?> languageCode)
{
    public JsonObject Decode(int contentTypeId, IReadOnlyDictionary<int, PropertyNode> nodes) =>
        DecodeProperties(contentTypeId, nodes, topLevel: true, options.Full);

    private JsonObject DecodeProperties(int? contentTypeId, IReadOnlyDictionary<int, PropertyNode> nodes, bool topLevel, bool full)
    {
        var ids = new HashSet<int>(nodes.Keys);
        if (topLevel && options.AllProperties && contentTypeId is { } typeId)
        {
            ids.UnionWith(model.PropertiesOf(typeId).Select(p => p.Id));
        }

        var definitions = ids
            .Select(id => model.Properties.GetValueOrDefault(id) ?? new PropertyDefinition(id, contentTypeId ?? 0, $"#{id}", "Unknown", PropertyBaseType.String, null, false, false))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

        var result = new JsonObject();
        foreach (var definition in definitions)
        {
            if (topLevel && options.Fields is { } fields && !fields.Contains(definition.Name))
            {
                continue;
            }

            var wholeValue = full || (topLevel && options.Fields is not null);
            var node = nodes.GetValueOrDefault(definition.Id);
            var decoded = node is null ? null : DecodeNode(definition, node, wholeValue);
            if (decoded is null)
            {
                if (topLevel && options.AllProperties)
                {
                    result[definition.Name] = new JsonObject { ["type"] = definition.TypeName, ["value"] = null };
                }
                continue;
            }

            if (topLevel && Culture(node!) is { } culture)
            {
                decoded["culture"] = culture;
            }
            result[definition.Name] = decoded;
        }
        return result;
    }

    private JsonObject? DecodeNode(PropertyDefinition definition, PropertyNode node, bool full)
    {
        if (definition.BaseType == PropertyBaseType.Block)
        {
            var blockType = definition.BlockType is { } type ? model.TypeName(type) : definition.TypeName;
            if (definition.IsList || node.Items.Count > 0)
            {
                var items = new JsonArray();
                foreach (var item in node.Items.Values)
                {
                    items.Add(DecodeProperties(item.TypeId ?? definition.BlockType, item.Properties, topLevel: false, full));
                }
                return items.Count == 0 ? null : new JsonObject { ["type"] = "BlockList", ["blockType"] = blockType, ["value"] = items };
            }
            var inner = DecodeProperties(definition.BlockType, node.Properties, topLevel: false, full);
            return inner.Count == 0 ? null : new JsonObject { ["type"] = "Block", ["blockType"] = blockType, ["value"] = inner };
        }

        if (node.Values.Count > 0)
        {
            var items = new JsonArray();
            foreach (var row in node.Values.Values)
            {
                items.Add(DecodeValue(definition, node, row, full)?["value"]?.DeepClone());
            }
            return new JsonObject { ["type"] = definition.TypeName, ["value"] = items };
        }

        return node.Row is null ? null : DecodeValue(definition, node, node.Row, full);
    }

    private JsonObject? DecodeValue(PropertyDefinition definition, PropertyNode node, PropertyRow row, bool full)
    {
        var result = new JsonObject { ["type"] = definition.TypeName };
        switch (definition.BaseType)
        {
            case PropertyBaseType.Boolean:
                return row.Boolean is { } flag ? With(result, flag) : null;
            case PropertyBaseType.Number:
                return row.Number is { } number ? With(result, number) : null;
            case PropertyBaseType.FloatNumber:
                return row.FloatNumber is { } real ? With(result, real) : null;
            case PropertyBaseType.Category:
                // The ids are stored apart from the row (tblContentCategory); the row only holds their key.
                return row.Categories is { Count: > 0 } categories
                    ? WithNode(result, new JsonArray(categories.Select(id => (JsonNode?)JsonValue.Create(model.CategoryName(id))).ToArray()))
                    : null;
            case PropertyBaseType.PageType:
                // The CMS stores the type id in the ContentType column; Number is read for values saved elsewhere.
                return (row.ContentType ?? row.Number) is { } typeId ? With(result, model.TypeName(typeId)) : null;
            case PropertyBaseType.Date:
                // Stored in UTC, like every CMS 12 date.
                return row.Date is { } date ? With(result, date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)) : null;
            case PropertyBaseType.PageReference:
            case PropertyBaseType.ContentReference:
                var target = row.ContentLink is { } id ? lookup.Content(id)
                    : row.LinkGuid is { } guid && guid != Guid.Empty ? lookup.Content(guid)
                    : null;
                return target is null ? null : WithNode(result, Reference(target));
        }

        var text = row.LongString ?? row.String;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        switch (definition.BaseType, definition.TypeName)
        {
            case (PropertyBaseType.LongString, "ContentArea"):
                var items = Fragments(text, node, full);
                return items.Count == 0 ? null : WithNode(result, items);
            case (PropertyBaseType.LongString, "XhtmlString"):
                TextValues.Put(result, text, full);
                AddLinks(result, text);
                if (Fragments(text, node, full) is { Count: > 0 } blocks)
                {
                    result["blocks"] = blocks;
                }
                return result;
            case (PropertyBaseType.LongString, "LinkItem"):
                return LinkMarkup.Parse(text) is [var link, ..] ? WithNode(result, Link(link)) : Text(result, text, full);
            case (PropertyBaseType.LinkCollection, _):
                var links = new JsonArray();
                foreach (var element in LinkMarkup.Parse(text))
                {
                    links.Add(Link(element));
                }
                return links.Count == 0 ? null : WithNode(result, links);
            case (PropertyBaseType.Json, _):
                return Json(result, definition, text, full);
            case (PropertyBaseType.LongString, not "LongString") when text.AsSpan().TrimStart() is ['[' or '{', ..]:
                // A custom property type (forms option lists, conditions, ...) that serialises itself as JSON
                // text; shown structured, like the list types stored in the JSON column.
                return Json(result, definition, text, full);
            default:
                return Text(result, text, full);
        }
    }

    /// <summary>ContentArea items, or blocks embedded in rich text.</summary>
    private JsonArray Fragments(string xhtml, PropertyNode node, bool full)
    {
        var items = new JsonArray();
        foreach (var fragment in ContentFragmentParser.Parse(xhtml))
        {
            JsonObject item;
            if (fragment.IsInline)
            {
                var stored = node.Items.GetValueOrDefault(fragment.Index);
                var typeId = fragment.InlineTypeId ?? stored?.TypeId;
                item = new JsonObject
                {
                    ["inline"] = true,
                    ["type"] = typeId is { } t ? model.TypeName(t) : null,
                    ["name"] = fragment.InlineName,
                };
                if (stored is not null)
                {
                    item["properties"] = DecodeProperties(typeId, stored.Properties, topLevel: false, full);
                }
            }
            else
            {
                var target = fragment.ContentGuid is { } guid ? lookup.Content(guid) : lookup.Content(fragment.ContentLink!.Value);
                item = Reference(target);
                if (target.Missing == true && fragment.Name is { } savedName)
                {
                    item["name"] = savedName;
                }
            }

            if (fragment.DisplayOption is { } displayOption)
            {
                item["displayOption"] = displayOption;
            }
            // Named as set --values takes them, so a ContentArea read here can be written back as is.
            if (fragment.ContentGroup is { } group)
            {
                item["group"] = group;
            }
            if (fragment.VisitorGroups.Count > 0)
            {
                item["visitorGroups"] = new JsonArray(fragment.VisitorGroups.Select(g => (JsonNode?)g).ToArray());
            }
            if (fragment.RenderSettings.Count > 0)
            {
                item["renderSettings"] = new JsonObject(fragment.RenderSettings.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value)));
            }
            items.Add(item);
        }
        return items;
    }

    private JsonObject Text(JsonObject result, string text, bool full)
    {
        TextValues.Put(result, text, full);
        if (PermanentLinks.Single(text) is { } guid)
        {
            result["target"] = Reference(lookup.Content(guid));
        }
        else
        {
            AddLinks(result, text);
        }
        return result;
    }

    private void AddLinks(JsonObject result, string text)
    {
        var links = new JsonArray();
        foreach (var link in PermanentLinks.Find(text).DistinctBy(l => (l.Guid, l.Anchor, l.Language)))
        {
            var entry = Identity(lookup.Content(link.Guid));
            if (link.Anchor is { } anchor)
            {
                entry["anchor"] = anchor;
            }
            if (link.Language is { } language)
            {
                entry["linkLanguage"] = language;
            }
            links.Add(entry);
        }
        if (links.Count > 0)
        {
            result["links"] = links;
        }
    }

    private JsonObject Link(LinkElement link)
    {
        var result = new JsonObject
        {
            ["text"] = link.Text,
            ["href"] = link.Href,
            ["title"] = link.Title,
            ["target"] = link.Target,
        };
        if (PermanentLinks.Find(link.Href) is [var permanent, ..])
        {
            result["content"] = Identity(lookup.Content(permanent.Guid));
        }
        foreach (var key in result.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            result.Remove(key);
        }
        return result;
    }

    private JsonObject? Json(JsonObject result, PropertyDefinition definition, string text, bool full)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return Text(result, text, full);
        }
        if (parsed is null)
        {
            return null;
        }

        if (definition.TypeName == "ContentReferenceList" && parsed is JsonArray references)
        {
            var targets = new JsonArray();
            foreach (var element in references)
            {
                var value = element?.ToString();
                targets.Add(Guid.TryParse(value, out var guid) ? Reference(lookup.Content(guid))
                    : int.TryParse(value?.Split('_')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? Reference(lookup.Content(id))
                    : element?.DeepClone());
            }
            return targets.Count == 0 ? null : WithNode(result, targets);
        }

        if (parsed is JsonArray { Count: 0 })
        {
            return null;
        }
        if (!full && TextValues.CutStrings(parsed))
        {
            result["truncated"] = true;
        }
        return WithNode(result, parsed);
    }

    private JsonObject Reference(ContentIdentity target)
    {
        var node = Identity(target);
        if (options.Expand && target.Missing != true && lookup.Expanded(target.Guid) is { } properties)
        {
            node["properties"] = properties.DeepClone();
        }
        return node;
    }

    private static JsonObject Identity(ContentIdentity target) => (JsonObject)JsonOutput.ToNode(target)!;

    /// <summary>The branch a top-level value comes from; for blocks with mixed rows, the non-master one.</summary>
    private string? Culture(PropertyNode node)
    {
        var languages = node.AllRows().Select(r => r.LanguageId).Distinct().ToList();
        var chosen = languages.Count == 0 ? (int?)null : languages.FirstOrDefault(l => l != masterLanguageId, languages[0]);
        return chosen is { } id ? languageCode(id) : null;
    }

    private static JsonObject With<T>(JsonObject result, T value)
    {
        result["value"] = JsonValue.Create(value);
        return result;
    }

    private static JsonObject WithNode(JsonObject result, JsonNode value)
    {
        result["value"] = value;
        return result;
    }
}
