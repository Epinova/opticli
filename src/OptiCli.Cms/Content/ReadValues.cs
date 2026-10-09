using System.Collections;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.SpecializedProperties;
using OptiCli.Cms.Compat;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// Renders property values in the <see cref="ContentItemProperty"/> shapes. Reads <see cref="PropertyData.Value"/>,
/// not the model's CLR getters, so values computed in code (fallbacks, defaults) don't hide what is stored.
/// </summary>
/// <param name="shown">
/// Which properties to render, of content or of a block in it: for an editor those the CMS edit UI shows them
/// (<see cref="EditUiProperties.Shown(IContentData, PropertyData)"/>); every one when null.
/// </param>
internal sealed class ReadValues(IContentTypeRepository types, Func<IContentData, PropertyData, bool>? shown = null)
{
    /// <summary>How the CMS serialises the items of list properties (<c>PropertyList&lt;T&gt;</c>) for storage.</summary>
    private static readonly JsonSerializerOptions StoredListOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>What <see cref="Format"/> returns for a value it has no shape for.</summary>
    private static readonly object Unrecognised = new();

    private readonly Dictionary<Type, IReadOnlyDictionary<string, PropertyDefinition>> _definitions = [];

    public Dictionary<string, ContentItemProperty> Properties(IContentData data)
    {
        var definitions = Definitions(data);
        var result = new Dictionary<string, ContentItemProperty>(StringComparer.Ordinal);
        foreach (var property in data.Property)
        {
            // CMS 13: a Visual Builder composition's storage is shown as the composition (ContentItem.Composition).
            if (property.IsMetaData || shown?.Invoke(data, property) == false || CmsCompositions.IsStorage(data, property))
            {
                continue;
            }
            var typeName = definitions.GetValueOrDefault(property.Name)?.Type?.Name ?? property.Type.ToString();
            result[property.Name] = Property(typeName, property, data.Property);
        }
        return result;
    }

    private ContentItemProperty Property(string typeName, PropertyData property, PropertyDataCollection owner)
    {
        switch (property.Value)
        {
            case Url:
                // Url.OriginalString drops the "~" of a stored permanent link (~/link/...); show what is stored.
                return new ContentItemProperty(typeName, Stored(property, owner));
            case BlockData block:
                return new ContentItemProperty("Block", Json(Properties(block))) { BlockType = BlockType(block) };
            case IEnumerable items and not string when items.Cast<object?>().ToList() is var list && list.Count > 0 && list.All(i => i is BlockData):
                return new ContentItemProperty("BlockList", Json(list.Select(i => Properties((BlockData)i!)).ToList()))
                {
                    BlockType = BlockType((BlockData)list[0]!),
                };
            // By name, as get prints them; the stored ids are local to one database.
            case CategoryList categories:
                return new ContentItemProperty(typeName, Json(PropertyValues.Categories(categories)));
            case int typeId when property is PropertyPageType:
                return new ContentItemProperty(typeName, Json(types.Load(typeId)?.Name ?? typeId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            case var value:
                var formatted = Format(value);
                return new ContentItemProperty(typeName, formatted == Unrecognised ? Stored(property, owner) : Json(formatted));
        }
    }

    private object? Format(object? value) => value switch
    {
        null => null,
        string text => text.Length == 0 ? null : text,
        // ContentArea derives from XhtmlString, so it must come first.
        ContentArea area => area.Items.Count == 0 ? null : area.Items.Select(AreaEntry).ToList(),
        XhtmlString html => html.IsEmpty ? null : html.ToInternalString(),
        ContentReference reference => ContentReference.IsNullOrEmpty(reference) ? null : reference.ToString(),
        LinkItemCollection links => links.Count == 0 ? null : links.Select(Link).ToList(),
        LinkItem link => Link(link),
        BlockData block => Properties(block),
        DateTime or DateTimeOffset or bool or int or long or double or decimal or float => value,
        IEnumerable items => items.Cast<object?>().Select(Format).ToList() is var list && list.Contains(Unrecognised) ? Unrecognised : list,
        _ => Unrecognised,
    };

    /// <summary>
    /// A custom property type's value as the property writes it to the database (<see cref="PropertyData.SaveData"/>):
    /// its items are the site's own classes, whose serialised shape only the property knows. Properties that store
    /// text (JSON, usually) return it as is; list properties return their items, which the CMS stores as camelCase JSON.
    /// </summary>
    private static JsonElement? Stored(PropertyData property, PropertyDataCollection owner) => CmsApi.SaveData(property, owner) switch
    {
        null => null,
        string { Length: 0 } => null,
        string text => ParsedJson(text) ?? Json(text),
        var items => Serialised(items),
    };

    private static JsonElement? Serialised(object value)
    {
        try
        {
            return JsonSerializer.SerializeToElement(value, value.GetType(), StoredListOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return Json(value.ToString());
        }
    }

    /// <summary>The text as a JSON array or object; null when it is anything else.</summary>
    private static JsonElement? ParsedJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ContentItemAreaEntry AreaEntry(ContentAreaItem item)
    {
        var inline = InlineBlocks.Of(item);
        var hasLink = !ContentReference.IsNullOrEmpty(item.ContentLink);
        return new ContentItemAreaEntry(
            hasLink ? item.ContentLink.ToString() : null,
            !hasLink && inline is null && item.ContentGuid != Guid.Empty ? item.ContentGuid : null)
        {
            Inline = inline is null ? null : true,
            Type = inline is null ? null : BlockType(inline),
            Name = inline is null ? null : InlineBlocks.Name(item),
            Properties = inline is null ? null : Properties(inline),
            DisplayOption = PropertyValues.DisplayOption(item),
            Group = string.IsNullOrWhiteSpace(item.ContentGroup) ? null : item.ContentGroup,
            VisitorGroups = item.AllowedRoles is { } roles && roles.Any() ? roles.ToList() : null,
        };
    }

    private static object Link(LinkItem link) => new { link.Href, link.Text, link.Title, link.Target };

    private string? BlockType(BlockData block) => types.Load(block.GetOriginalType())?.Name;

    private IReadOnlyDictionary<string, PropertyDefinition> Definitions(IContentData data)
    {
        var type = data.GetOriginalType();
        if (!_definitions.TryGetValue(type, out var definitions))
        {
            var contentType = data is IContent content ? types.Load(content.ContentTypeID) : types.Load(type);
            definitions = (contentType?.PropertyDefinitions ?? [])
                .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _definitions[type] = definitions;
        }
        return definitions;
    }

    private static JsonElement? Json(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), AgentJson.Options);
}
