using System.Globalization;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.SpecializedProperties;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Content;

/// <summary>Applies a request's property map to a writable content instance.</summary>
internal sealed class PropertyWriter(ContentLocator locator, BlockFactory blocks)
{
    /// <summary>Pseudo-property for the content name in snapshots and diffs.</summary>
    public const string NameKey = "Name";

    /// <summary>Built-in metadata an agent may reasonably need to set; all other metadata is refused.</summary>
    public static readonly HashSet<string> WritableMetadata = new(StringComparer.OrdinalIgnoreCase) { "PageURLSegment", "PageVisibleInMenu" };

    public const string StartPublishKey = "StartPublish";
    public const string StopPublishKey = "StopPublish";

    /// <summary>
    /// The publish dates of versionable content (<see cref="IVersionable"/>), by their own names and the pages' metadata
    /// names. Lists and archives often sort and filter by <c>StartPublish</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PublishDates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [StartPublishKey] = StartPublishKey,
        ["PageStartPublish"] = StartPublishKey,
        [StopPublishKey] = StopPublishKey,
        ["PageStopPublish"] = StopPublishKey,
    };

    public void Apply(IContentData content, IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null)
        {
            return;
        }
        foreach (var (name, value) in values)
        {
            Set(content, name, value);
        }
    }

    public static PropertyData Find(IContentData content, string name)
    {
        var property = content.Property.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property is null)
        {
            var known = content.Property.Where(p => !p.IsMetaData).Select(p => p.Name);
            throw AgentException.Usage($"'{name}' is not a property of {content.GetOriginalType().Name}.",
                $"Properties: {string.Join(", ", known)}.");
        }
        if (property.IsMetaData && !WritableMetadata.Contains(property.Name))
        {
            throw AgentException.Usage($"'{property.Name}' is built-in metadata and can't be set through opticli.",
                "Use the request's name field to rename content.");
        }
        return property;
    }

    private void Set(IContentData content, string name, JsonElement value)
    {
        if (name.Equals(NameKey, StringComparison.OrdinalIgnoreCase) && content is IContent named && content.Property.All(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            named.Name = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw AgentException.Usage("Name must be a string.");
            return;
        }

        if (PublishDates.TryGetValue(name, out var date) && content is IVersionable versionable
            && content.Property.All(p => p.IsMetaData || !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            SetPublishDate(versionable, date, value);
            return;
        }

        var property = Find(content, name);
        if (content is ILocalizable { Language: { } language, MasterLanguage: { } master }
            && !language.Equals(master) && !property.IsLanguageSpecific)
        {
            throw AgentException.Usage($"'{property.Name}' is not culture-specific, so it can only be changed on the master language ({master.Name}).");
        }

        try
        {
            SetValue(property, value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException or JsonException or OverflowException or InvalidPropertyValueException)
        {
            throw AgentException.Usage($"Can't set '{property.Name}' ({property.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// An ISO 8601 date or time; without an offset it is the site's local time. <c>null</c> clears it: a cleared
    /// <c>StartPublish</c> is set to the time of publishing by the CMS.
    /// </summary>
    private static void SetPublishDate(IVersionable versionable, string which, JsonElement value)
    {
        DateTime? parsed = value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when value.GetString() is { Length: 0 } => null,
            JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at) => at.LocalDateTime,
            _ => throw AgentException.Usage($"{which} must be a date or time like 2025-02-14 or 2025-02-14T08:00:00+01:00, or null."),
        };
        if (which == StartPublishKey)
        {
            versionable.StartPublish = parsed;
        }
        else
        {
            versionable.StopPublish = parsed;
        }
    }

    private void SetValue(PropertyData property, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                property.Clear();
                return;
            case JsonValueKind.Array when property is PropertyContentArea:
                property.Value = BuildArea(value.Deserialize<List<AreaItemValue>>(AgentJson.Options)!);
                return;
            case JsonValueKind.Array when property is PropertyLinkCollection:
                property.Value = BuildLinks(value.Deserialize<List<LinkItemValue>>(AgentJson.Options)!);
                return;
            case JsonValueKind.Object when property.PropertyValueType == typeof(LinkItem):
                property.Value = NewLink(value.Deserialize<LinkItemValue>(AgentJson.Options)!);
                return;
            case JsonValueKind.String when property.PropertyValueType == typeof(LinkItem) && !value.GetString()!.TrimStart().StartsWith('<'):
                // A bare href or ref keeps the link's text; ParseToSelf only accepts the stored <a> markup.
                var current = property.Value as LinkItem;
                property.Value = NewLink(new LinkItemValue { Href = value.GetString()!, Text = current?.Text, Title = current?.Title, Target = current?.Target });
                return;
            case JsonValueKind.Object when property.Value is IContentData block:
                Apply(block, value.Deserialize<Dictionary<string, JsonElement>>(AgentJson.Options));
                return;
            case JsonValueKind.String when property is PropertyContentReference or PropertyPageReference:
                // Accept GUIDs too, which ParseToSelf doesn't.
                property.ParseToSelf(locator.ResolveContent(value.GetString(), $"reference for {property.Name}").ToString());
                return;
            case JsonValueKind.String:
                property.ParseToSelf(value.GetString());
                return;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                property.ParseToSelf(value.GetRawText());
                return;
            default:
                property.Value = Structured(property, value);
                return;
        }
    }

    /// <summary>Lists and other structured values: deserialized straight into the property's value type.</summary>
    private object? Structured(PropertyData property, JsonElement value)
    {
        var type = property.PropertyValueType;
        var element = type.IsGenericType && type.GetGenericArguments() is [var arg] && typeof(IEnumerable<>).MakeGenericType(arg).IsAssignableFrom(type) ? arg : null;

        if (element is not null && typeof(BlockData).IsAssignableFrom(element) && value.ValueKind == JsonValueKind.Array)
        {
            return BlockList(property, element, value);
        }
        if (element == typeof(ContentReference) && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(item => locator.ResolveContent(item.ValueKind == JsonValueKind.Number ? item.GetRawText() : item.GetString(), $"reference in {property.Name}"))
                .ToList();
        }

        // Interfaces like IList<string> can't be instantiated; a List<T> satisfies them.
        var target = element is not null && type.IsInterface ? typeof(List<>).MakeGenericType(element) : type;
        return value.Deserialize(target, AgentJson.Options);
    }

    /// <summary>A block list: one new block per object, with the object's values set like a local block's.</summary>
    private System.Collections.IList BlockList(PropertyData property, Type element, JsonElement value)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw AgentException.Usage($"{property.Name}[{index}] must be an object of {element.Name} property names to values, e.g. {{\"Name\": \"...\"}}.");
            }
            var block = blocks.Create(element);
            Apply(block, item.Deserialize<Dictionary<string, JsonElement>>(AgentJson.Options));
            list.Add(block);
            index++;
        }
        return list;
    }

    public ContentArea BuildArea(IEnumerable<AreaItemValue> items)
    {
        var area = new ContentArea();
        foreach (var item in items)
        {
            area.Items.Add(NewAreaItem(item.Guid?.ToString() ?? item.Ref, item.DisplayOption));
        }
        return area;
    }

    public ContentAreaItem NewAreaItem(string? reference, string? displayOption)
    {
        var link = locator.ResolveContent(reference, "ContentArea item");
        var target = locator.LoadAnyLanguage(link);
        // Link only: on newer CMS versions setting ContentGuid clears ContentLink (they are alternatives).
        var item = new ContentAreaItem { ContentLink = target.ContentLink.ToReferenceWithoutVersion() };
        if (!string.IsNullOrWhiteSpace(displayOption))
        {
            // Null until something is set on a new item.
            item.RenderSettings ??= new Dictionary<string, object>();
            item.RenderSettings[PropertyValues.DisplayOptionKey] = displayOption;
        }
        return item;
    }

    private LinkItemCollection BuildLinks(IEnumerable<LinkItemValue> links)
    {
        var collection = new LinkItemCollection();
        foreach (var link in links)
        {
            collection.Add(NewLink(link));
        }
        return collection;
    }

    private LinkItem NewLink(LinkItemValue link) => new()
    {
        Href = RefSyntax.TryParse(link.Href, out _) ? PermanentLink(link.Href) : link.Href,
        Text = link.Text,
        Title = link.Title,
        Target = link.Target,
    };

    /// <summary>Internal links are stored as permanent links, like the editor stores them.</summary>
    private string PermanentLink(string reference)
    {
        var target = locator.LoadAnyLanguage(locator.ResolveContent(reference, "link"));
        return $"~/link/{target.ContentGuid.ToString("N", CultureInfo.InvariantCulture)}.aspx";
    }
}
