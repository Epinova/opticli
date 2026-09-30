using System.Collections;
using System.Globalization;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.ServiceLocation;
using EPiServer.SpecializedProperties;
using OptiCli.Protocol;

namespace OptiCli.Agent.Content;

/// <summary>
/// Renders property values as JSON in the same shapes the draft endpoint accepts, so a diff's
/// <c>after</c> can be sent back as-is.
/// </summary>
internal static class PropertyValues
{
    public const string DisplayOptionKey = "data-epi-content-display-option";

    /// <summary>Content name plus every editable property, keyed by name (case-insensitive).</summary>
    public static Dictionary<string, JsonElement?> Snapshot(IContentData content)
    {
        var values = new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase);
        if (content is IContent named)
        {
            values[PropertyWriter.NameKey] = ToJson(Format(named.Name));
        }
        if (content is IVersionable versionable)
        {
            values[PropertyWriter.StartPublishKey] = ToJson(versionable.StartPublish);
            values[PropertyWriter.StopPublishKey] = ToJson(versionable.StopPublish);
        }
        if (content is PageData page)
        {
            values[PropertyWriter.ChildSortOrderKey] = ToJson(page.ChildSortOrder.ToString());
            values[PropertyWriter.SortIndexKey] = ToJson(page.SortIndex);
            values[PropertyWriter.ShortcutKey] = ToJson(Shortcut(page));
            // As get shows it: /campaign (the CMS stores ~/campaign; ExternalURL gives campaign).
            values[PropertyWriter.SimpleAddressKey] = ToJson(page.ExternalURL?.Trim().TrimStart('~').Trim('/') is { Length: > 0 } address ? "/" + address : null);
        }
        if (content is ICategorizable { Category: var category })
        {
            values[PropertyWriter.CategoryKey] = ToJson(Categories(category));
        }
        foreach (var property in content.Property)
        {
            if (!property.IsMetaData || PropertyWriter.WritableMetadata.Contains(property.Name))
            {
                values[property.Name] = ToJson(property is PropertyCategory categories ? Categories(categories.Category) : Format(property.Value));
            }
        }
        return values;
    }

    /// <summary>Properties whose JSON differs, in the order of <paramref name="after"/>.</summary>
    public static List<PropertyChange> Diff(IReadOnlyDictionary<string, JsonElement?> before, IReadOnlyDictionary<string, JsonElement?> after)
    {
        var changes = new List<PropertyChange>();
        foreach (var (name, value) in after)
        {
            var previous = before.GetValueOrDefault(name);
            if (previous?.GetRawText() != value?.GetRawText())
            {
                changes.Add(new PropertyChange(name, previous, value));
            }
        }
        return changes;
    }

    /// <summary>Category names, as the writer takes them; null when there are none.</summary>
    private static string[]? Categories(CategoryList? categories)
    {
        if (categories is not { Count: > 0 })
        {
            return null;
        }
        // Not PropertyCategory.CategoryNames: it caches the names it first read and needs a parent.
        var repository = ServiceLocator.Current.GetInstance<CategoryRepository>();
        return categories.Select(id => repository.Get(id)?.Name ?? id.ToString(CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>The shortcut as the writer takes it; null for a normal page.</summary>
    private static Dictionary<string, string?>? Shortcut(PageData page)
    {
        if (page.LinkType == PageShortcutType.Normal)
        {
            return null;
        }
        var target = page.Property["PageShortcutLink"]?.Value as ContentReference;
        var shortcut = new Dictionary<string, string?> { ["type"] = ShortcutValue.CamelCase(page.LinkType.ToString()) };
        if (page.LinkType is PageShortcutType.Shortcut or PageShortcutType.FetchData && !ContentReference.IsNullOrEmpty(target))
        {
            shortcut["to"] = target.ToReferenceWithoutVersion().ToString();
        }
        if (page.LinkType == PageShortcutType.External)
        {
            // As saved to the database (a permanent link stays one); LinkURL gives the friendly URL.
            shortcut["url"] = page.Property["PageLinkURL"]?.SaveData(page.Property)?.ToString() ?? page.LinkURL;
        }
        if (page.Property["PageTargetFrame"] is PropertyFrame { IsNull: false } frame)
        {
            shortcut["target"] = PropertyWriter.FrameTarget(frame.FrameName);
        }
        return shortcut;
    }

    public static object? Format(object? value) => value switch
    {
        null => null,
        string text => text.Length == 0 ? null : text,
        ContentArea area => area.Items.Select(item => new AreaItemValue
        {
            // Inline blocks (CMS 12.20+) have neither a link nor a GUID; they show up with no ref.
            Ref = ContentReference.IsNullOrEmpty(item.ContentLink) ? null : item.ContentLink.ToString(),
            Guid = ContentReference.IsNullOrEmpty(item.ContentLink) && item.ContentGuid != Guid.Empty ? item.ContentGuid : null,
            DisplayOption = DisplayOption(item),
        }).ToList(),
        ContentReference reference => ContentReference.IsNullOrEmpty(reference) ? null : reference.ToString(),
        LinkItemCollection links => links.Select(link => new LinkItemValue
        {
            Href = link.Href,
            Text = link.Text,
            Title = link.Title,
            Target = link.Target,
        }).ToList(),
        LinkItem link => new LinkItemValue
        {
            Href = link.Href,
            Text = link.Text,
            Title = link.Title,
            Target = link.Target,
        },
        XhtmlString html => html.IsEmpty ? null : html.ToInternalString(),
        IContentData block => Snapshot(block),
        Url url => url.IsEmpty() ? null : url.OriginalString,
        DateTime or bool or int or long or double or decimal or float => value,
        IEnumerable items => items.Cast<object?>().Select(Format).ToList(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        // Custom values (PropertyList<T> items) are compared by their JSON; ToString() would only give the type name.
        _ => value,
    };

    public static string? DisplayOption(ContentAreaItem item) =>
        item.RenderSettings is { } settings && settings.TryGetValue(DisplayOptionKey, out var option) ? option?.ToString() : null;

    private static JsonElement? ToJson(object? value)
    {
        if (value is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.SerializeToElement(value, value.GetType(), AgentJson.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return JsonSerializer.SerializeToElement(value.ToString(), AgentJson.Options);
        }
    }
}
