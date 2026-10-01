using System.Globalization;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Filters;
using EPiServer.SpecializedProperties;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using OptiCli.Agent.Http;
using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Agent.Content;

/// <summary>Applies a request's property map to a writable content instance.</summary>
internal sealed class PropertyWriter(
    ContentLocator locator, BlockFactory blocks, CategoryRepository categories, IFrameRepository frames, DisplayOptions displayOptions)
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

    public const string ChildSortOrderKey = "ChildSortOrder";
    public const string SortIndexKey = "SortIndex";

    /// <summary>
    /// A page's sort order for its children and its own sort index among its siblings (<see cref="PageData.ChildSortOrder"/>,
    /// <see cref="PageData.SortIndex"/>), by those names and their metadata names. List pages often show children in this order.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Sorting = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [ChildSortOrderKey] = ChildSortOrderKey,
        ["PageChildOrderRule"] = ChildSortOrderKey,
        [SortIndexKey] = SortIndexKey,
        ["PagePeerOrder"] = SortIndexKey,
    };

    public const string ShortcutKey = "Shortcut";
    public const string SimpleAddressKey = "SimpleAddress";

    /// <summary>
    /// A page's shortcut (link type, target, window) and simple address, the latter also by its <see cref="PageData"/>
    /// and metadata names.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PageLinks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [ShortcutKey] = ShortcutKey,
        [SimpleAddressKey] = SimpleAddressKey,
        ["ExternalURL"] = SimpleAddressKey,
        ["PageExternalURL"] = SimpleAddressKey,
    };

    public const string CategoryKey = "Category";

    /// <summary>The built-in category of pages, shared blocks and media (<see cref="ICategorizable"/>), also by the pages' metadata name.</summary>
    public static readonly IReadOnlyDictionary<string, string> BuiltInCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [CategoryKey] = CategoryKey,
        ["PageCategory"] = CategoryKey,
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

        var ownProperty = content.Property.Any(p => !p.IsMetaData && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!ownProperty && (Sorting.TryGetValue(name, out var setting) || PageLinks.TryGetValue(name, out setting)))
        {
            if (content is not PageData page)
            {
                throw AgentException.Usage($"'{setting}' is a page setting; {content.GetOriginalType().Name} is not a page.");
            }
            if (Sorting.ContainsKey(setting))
            {
                if (!page.IsMasterLanguageBranch)
                {
                    throw AgentException.Usage($"'{setting}' is not culture-specific, so it can only be changed on the master language ({page.MasterLanguage?.Name}).");
                }
                SetSorting(page, setting, value);
            }
            else if (setting == ShortcutKey)
            {
                SetShortcut(page, ShortcutValue.Parse(value));
            }
            else
            {
                page.ExternalURL = SimpleAddress(value);
            }
            return;
        }

        if (!ownProperty && BuiltInCategory.ContainsKey(name))
        {
            if (content is not ICategorizable categorizable)
            {
                throw AgentException.Usage($"{content.GetOriginalType().Name} has no built-in category; pages, shared blocks and media have one.");
            }
            if (content is ILocalizable { Language: { } branch, MasterLanguage: { } masterBranch } && !branch.Equals(masterBranch)
                && content.Property.OfType<PropertyCategory>().FirstOrDefault(p => p.IsMetaData) is { IsLanguageSpecific: false })
            {
                throw AgentException.Usage($"'{CategoryKey}' is not culture-specific, so it can only be changed on the master language ({masterBranch.Name}).");
            }
            categorizable.Category = new CategoryList(Categories(value, CategoryKey));
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

    private static void SetSorting(PageData page, string which, JsonElement value)
    {
        if (which == ChildSortOrderKey)
        {
            page.ChildSortOrder = (FilterSortOrder)PageSorting.ParseChildSortOrder(value);
        }
        else
        {
            page.SortIndex = PageSorting.ParseSortIndex(value);
        }
    }

    /// <summary>
    /// Sets the link type, the page it points at, the external URL and the window, as edit mode's shortcut dialog does.
    /// Saving sets the link of every type but external (the page's own, the target's, or <c>#</c> when inactive).
    /// </summary>
    private void SetShortcut(PageData page, ShortcutValue shortcut)
    {
        var type = Enum.Parse<PageShortcutType>(shortcut.Type);
        var link = page.Property["PageShortcutLink"];
        link.Clear();
        if (type is PageShortcutType.Shortcut or PageShortcutType.FetchData)
        {
            var target = locator.LoadAnyLanguage(locator.ResolveContent(shortcut.To, "shortcut target"));
            link.Value = target is PageData targetPage
                ? targetPage.PageLink.ToPageReference().ToReferenceWithoutVersion()
                : throw AgentException.Usage($"A shortcut must point at a page; {shortcut.To} is {target.GetOriginalType().Name}.");
        }
        else if (type == PageShortcutType.External)
        {
            var anchor = shortcut.Anchor is { } fragment ? "#" + fragment : "";
            page.LinkURL = (shortcut.To is { } to ? PermanentLink(to) : shortcut.Url) + anchor;
        }
        page.LinkType = type;

        var frame = page.Property["PageTargetFrame"];
        if (shortcut.Target is null)
        {
            frame.Clear();
        }
        else
        {
            frame.Value = Frame(shortcut.Target).ID;
        }
    }

    /// <summary>A window by name (<c>_blank</c>, as stored: <c>target="_blank"</c>), description or id.</summary>
    private Frame Frame(string name)
    {
        var all = frames.List().ToList();
        return all.FirstOrDefault(f => f.ID.ToString(CultureInfo.InvariantCulture) == name
                || FrameTarget(f.Name).Equals(name, StringComparison.OrdinalIgnoreCase)
                || f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Description, name, StringComparison.OrdinalIgnoreCase))
            ?? throw AgentException.Usage($"No window '{name}'.", $"Windows: {string.Join(", ", all.Select(f => FrameTarget(f.Name)))}.");
    }

    /// <summary><c>_blank</c> from a frame name stored as <c>target="_blank"</c>.</summary>
    public static string FrameTarget(string? name)
    {
        var value = name?.Trim() ?? "";
        return value.StartsWith("target=", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim('"', '\'', ' ') : value;
    }

    /// <summary>A simple address as the CMS stores it (<c>~/campaign</c>) from <c>campaign</c>, <c>/campaign</c> or <c>~/campaign</c>; empty clears it.</summary>
    private static string? SimpleAddress(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Null => "",
            JsonValueKind.String => value.GetString()!.Trim(),
            _ => throw AgentException.Usage("SimpleAddress must be a string like \"campaign\", or empty to clear it."),
        };
        var path = text.TrimStart('~').Trim('/');
        if (path.Length == 0)
        {
            return null;
        }
        return path.Contains("://", StringComparison.Ordinal) || path.Any(c => char.IsWhiteSpace(c) || c is '?' or '#')
            ? throw AgentException.Usage($"'{text}' is not a simple address: give a path like \"campaign\" (no host, query or spaces).")
            : "~/" + path;
    }

    /// <summary>Categories by name (<c>Name</c>, else the display name), or id; an array or a comma-separated string.</summary>
    private List<int> Categories(JsonElement value, string what)
    {
        var items = value.ValueKind switch
        {
            JsonValueKind.Null => [],
            JsonValueKind.Array => value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number ? item.GetRawText() : item.GetString() ?? "").ToList(),
            JsonValueKind.String => value.GetString()!.Split(',').ToList(),
            JsonValueKind.Number => [value.GetRawText()],
            _ => throw AgentException.Usage($"{what} takes category names: [\"News\", \"Events\"] or \"News,Events\"."),
        };
        var all = categories.GetRoot().GetList().Cast<Category>().ToList();
        var ids = new List<int>();
        foreach (var item in items.Select(i => i.Trim()).Where(i => i.Length > 0))
        {
            var match = all.FirstOrDefault(c => c.ID.ToString(CultureInfo.InvariantCulture) == item || c.Name.Equals(item, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(c => string.Equals(c.Description, item, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var names = all.Where(c => c.Selectable).Select(c => c.Name).ToList();
                var suggestion = Suggestions.DidYouMean(item, names);
                var list = names.Count == 0 ? "The site has no categories (they're made in admin mode)." : $"Categories: {string.Join(", ", names)}.";
                throw AgentException.Usage($"No category '{item}'.", suggestion is null ? list : $"{suggestion} {list}");
            }
            if (!match.Selectable)
            {
                throw AgentException.Usage($"Category '{match.Name}' is not selectable (edit mode doesn't offer it).");
            }
            if (!ids.Contains(match.ID))
            {
                ids.Add(match.ID);
            }
        }
        return ids;
    }

    private void SetValue(PropertyData property, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                property.Clear();
                return;
            case JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Number when property is PropertyCategory:
                // By name as well as id, unlike ParseToSelf; an empty list clears it.
                var ids = Categories(value, property.Name);
                if (ids.Count == 0)
                {
                    property.Clear();
                }
                else
                {
                    property.Value = new CategoryList(ids);
                }
                return;
            case JsonValueKind.Array when property is PropertyContentArea:
                property.Value = BuildArea(value.Deserialize<List<AreaItemValue>>(AgentJson.Options)!, property.Value as ContentArea);
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

    /// <summary>
    /// The area of <paramref name="items"/>, replacing <paramref name="current"/>. Each item takes over the current item
    /// for the same content that <see cref="AreaItemRules.Match"/> pairs it with: its group and visitor groups unless
    /// the item gives them, and its other render settings. Without that, writing back what <c>get</c> shows would lose
    /// the area's personalization.
    /// </summary>
    public ContentArea BuildArea(IReadOnlyList<AreaItemValue> items, ContentArea? current)
    {
        var links = items.Select(item => Target(item.Guid?.ToString() ?? item.Ref)).ToList();
        var currentItems = current?.Items.ToList() ?? [];
        var matches = AreaItemRules.Match(
            currentItems.Select(item => ContentReference.IsNullOrEmpty(item.ContentLink) ? null : item.ContentLink.ToReferenceWithoutVersion().ToString()).ToList(),
            links.Select(link => link.ToString()).ToList());

        var area = new ContentArea();
        for (var i = 0; i < items.Count; i++)
        {
            var takenOver = matches[i] < 0 ? null : currentItems[matches[i]];
            var item = NewAreaItem(links[i], items[i].DisplayOption, takenOver);
            if (items[i].VisitorGroups is { } given)
            {
                AreaItemRules.RequireVisitorGroups(given, VisitorGroupName);
            }
            var (group, visitorGroups) = AreaItemRules.Personalization(items[i], takenOver?.ContentGroup, takenOver?.AllowedRoles);
            if (group is not null)
            {
                item.ContentGroup = group;
            }
            if (visitorGroups.Count > 0)
            {
                item.AllowedRoles = visitorGroups;
            }
            area.Items.Add(item);
        }
        return area;
    }

    /// <summary>The name of the visitor group with this id; null when there is none (or the site has no personalization).</summary>
    private static string? VisitorGroupName(Guid id) =>
        ServiceLocator.Current.TryGetExistingInstance(out EPiServer.Personalization.VisitorGroups.IVisitorGroupRepository? groups) && groups is not null ? groups.Load(id)?.Name : null;

    public ContentAreaItem NewAreaItem(string? reference, string? displayOption) => NewAreaItem(Target(reference), displayOption, null);

    /// <summary>The content an item shows, without version.</summary>
    private ContentReference Target(string? reference) =>
        locator.LoadAnyLanguage(locator.ResolveContent(reference, "ContentArea item")).ContentLink.ToReferenceWithoutVersion();

    /// <param name="takenOver">The current item this one replaces: its render settings other than the display option are kept.</param>
    private ContentAreaItem NewAreaItem(ContentReference link, string? displayOption, ContentAreaItem? takenOver)
    {
        // Link only: on newer CMS versions setting ContentGuid clears ContentLink (they are alternatives).
        var item = new ContentAreaItem { ContentLink = link };
        foreach (var (key, value) in takenOver?.RenderSettings ?? new Dictionary<string, object>())
        {
            if (key != PropertyValues.DisplayOptionKey)
            {
                // Null until something is set on a new item.
                item.RenderSettings ??= new Dictionary<string, object>();
                item.RenderSettings[key] = value;
            }
        }
        if (DisplayOptionId(displayOption, takenOver is null ? null : PropertyValues.DisplayOption(takenOver)) is { } id)
        {
            item.RenderSettings ??= new Dictionary<string, object>();
            item.RenderSettings[PropertyValues.DisplayOptionKey] = id;
        }
        return item;
    }

    /// <summary>The registered display option <paramref name="given"/> names; the one stored on the item it replaces passes as is.</summary>
    private string? DisplayOptionId(string? given, string? stored)
    {
        if (string.IsNullOrWhiteSpace(given))
        {
            return null;
        }
        // So a value read back with get can be written back even if the site no longer registers it.
        if (given == stored)
        {
            return given;
        }
        return AreaItemRules.DisplayOption(given, displayOptions.Select(o => new AreaItemRules.Option(o.Id, o.Name, o.Tag)).ToList());
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
