using System.Globalization;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Filters;
using EPiServer.SpecializedProperties;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using OptiCli.Core.Text;
using OptiCli.Cms.Compat;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>Applies a request's property map to a writable content instance.</summary>
/// <remarks>
/// For an editor, a value must also be one the CMS edit UI would let them set: only properties it shows them as
/// editable (<see cref="EditUiProperties"/>), no script in rich text or links (<see cref="CmsCall.MayWriteScript"/>),
/// and content references only to content they can read (<see cref="CmsCall.MayReferenceUnchecked"/>).
/// </remarks>
internal sealed class PropertyWriter(
    CmsCall call, ContentLocator locator, BlockFactory blocks, CategoryRepository categories, IFrameRepository frames, DisplayOptions displayOptions)
{
    /// <summary>Pseudo-property for the content name in snapshots and diffs.</summary>
    public const string NameKey = "Name";

    /// <summary>
    /// For an editor: each block of a block list being written, with the item it replaces (the one at its index, of its
    /// type). What that item had is what the new one may keep (<see cref="CheckValue"/>), and its values the editor can't
    /// see or change are carried over (<see cref="BlockList"/>).
    /// </summary>
    private readonly Dictionary<IContentData, IContentData> _blockBaselines = new(ReferenceEqualityComparer.Instance);

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
        foreach (var name in values.Keys)
        {
            if (AreaItemRules.Indexed(name) is { } item && values.Keys.FirstOrDefault(k => k.Equals(item.Property, StringComparison.OrdinalIgnoreCase)) is { } whole)
            {
                throw AgentException.Usage($"'{name}' changes one item of '{whole}', which is also given whole.",
                    $"Give the item's values in the whole area (its properties), or leave '{whole}' out.");
            }
        }
        foreach (var (name, value) in values)
        {
            Set(content, name, value);
        }
    }

    /// <summary>The property to set, by name (any case): one the caller may change.</summary>
    /// <exception cref="AgentException"><c>usage</c> for no such property, built-in metadata, or one the edit UI doesn't let an editor change.</exception>
    public PropertyData Find(IContentData content, string name)
    {
        var property = content.Property.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property is null)
        {
            var known = content.Property.Where(p => !p.IsMetaData && call.Properties.Shown(content, p)).Select(p => p.Name);
            throw AgentException.Usage($"'{name}' is not a property of {content.GetOriginalType().Name}.",
                $"Properties: {string.Join(", ", known)}.");
        }
        if (CmsCompositions.IsStorage(content, property))
        {
            throw AgentException.Usage($"'{property.Name}' is where {content.GetOriginalType().Name} stores its Visual Builder composition; it isn't set directly.",
                call.ForCaller("Change the composition with compositionOps or composition (opticli composition, or composition in --values).", "Change the composition instead."));
        }
        if (property.IsMetaData && !WritableMetadata.Contains(property.Name))
        {
            throw AgentException.Usage($"'{property.Name}' is built-in metadata and can't be set through opticli.",
                "Use the request's name field to rename content.");
        }
        call.Properties.RequireEditable(content, property);
        return property;
    }

    private void Set(IContentData content, string name, JsonElement value)
    {
        if (AreaItemRules.Indexed(name) is { } item)
        {
            SetInlineItem(content, item.Property, item.Index, value);
            return;
        }
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
        RequireLanguage(content, property);

        // What the value had before, which an editor may write back as it was (see CheckValue).
        var before = Collect(_blockBaselines.TryGetValue(content, out var previous) ? previous.Property[property.Name]?.Value : property.Value);
        try
        {
            SetValue(property, value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException or JsonException or OverflowException or InvalidPropertyValueException)
        {
            throw AgentException.Usage($"Can't set '{property.Name}' ({property.GetType().Name}): {ex.Message}");
        }
        CheckValue(property, before);
    }

    private static void RequireLanguage(IContentData content, PropertyData property)
    {
        if (content is ILocalizable { Language: { } language, MasterLanguage: { } master }
            && !language.Equals(master) && !property.IsLanguageSpecific)
        {
            throw AgentException.Usage($"'{property.Name}' is not culture-specific, so it can only be changed on the master language ({master.Name}).");
        }
    }

    /// <summary>
    /// <c>MainArea[2]</c>: sets <paramref name="value"/>'s values on the inline block at that position of the ContentArea,
    /// on a copy of the area; its other values and the item's render settings stay as they are.
    /// </summary>
    /// <exception cref="AgentException"><c>usage</c> for no such item, or one that isn't an inline block.</exception>
    private void SetInlineItem(IContentData content, string name, int index, JsonElement value)
    {
        var property = Find(content, name);
        var path = $"{property.Name}[{index}]";
        if (property is not PropertyContentArea)
        {
            throw AgentException.Usage($"'{path}' names an item of a ContentArea, and '{property.Name}' is a {property.GetType().Name}.",
                "Positions in brackets are for ContentArea items; set a block list as a whole array.");
        }
        RequireLanguage(content, property);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw AgentException.Usage($"'{path}' takes an object of the inline block's property names to values, e.g. {{\"Heading\": \"...\"}}.",
                $"To replace or remove the item, write '{property.Name}' whole, or use {call.ForCaller("`opticli area`", "areaOps")}.");
        }
        var area = property.Value is ContentArea existing ? (ContentArea)((EPiServer.Data.Entity.IReadOnly)existing).CreateWritableClone() : new ContentArea();
        var items = area.Items;
        if (index >= items.Count)
        {
            throw AgentException.Usage($"'{property.Name}' has {items.Count} item(s), so there is no {path} (positions are zero-based).");
        }
        var item = items[index];
        if (InlineBlocks.Of(item) is not { } block)
        {
            var shown = ContentReference.IsNullOrEmpty(item.ContentLink) ? null : item.ContentLink.ToReferenceWithoutVersion().ToString();
            throw AgentException.Usage($"{path} is {(shown is null ? "an item without content" : $"the shared block {shown}")}, not an inline block, so it has no values of its own in the area.",
                shown is null ? null : call.ForCaller($"Change the block itself: opticli set {shown} Prop=value.", $"Change the block itself (content {shown})."));
        }
        if (block is EPiServer.Data.Entity.IReadOnly { IsReadOnly: true } readOnly)
        {
            block = (BlockData)readOnly.CreateWritableClone();
            InlineBlocks.Set(item, block, InlineBlocks.TypeId(block));
        }
        ApplyTo(block, value.Deserialize<Dictionary<string, JsonElement>>(AgentJson.Options), path);
        property.Value = area;
    }

    /// <summary>Sets values on a block in a ContentArea, with errors saying which item it is.</summary>
    private void ApplyTo(BlockData block, IReadOnlyDictionary<string, JsonElement>? values, string where)
    {
        try
        {
            Apply(block, values);
        }
        catch (AgentException ex) when (ex.Code is AgentErrorCodes.Usage or AgentErrorCodes.NotFound)
        {
            throw new AgentException(ex.Code, $"{where}: {ex.Message}", ex.Hint) { Validation = ex.Validation };
        }
    }

    /// <summary>
    /// For an editor, the value as set, whatever form it was given in: rich text without script and with embedded blocks
    /// only from content they can read, links (also in a list property's items) with a web, mail or phone scheme or none
    /// and no attributes but href, title and target. What the property already had, construct by construct and as often
    /// as it had it, may be written back as it was: rich text with a video's <c>&lt;iframe&gt;</c>, or an sms: link, is
    /// still the editor's to correct a typo in. Anything new is refused, also an old construct copied once more.
    /// </summary>
    /// <param name="before">What the value had before the write (<see cref="Collect(object?)"/>).</param>
    /// <exception cref="AgentException"><c>usage</c> naming what was found; <c>not_found</c> for an embedded block they can't read.</exception>
    private void CheckValue(PropertyData property, Constructs before)
    {
        if (call.MayWriteScript && call.MayReferenceUnchecked)
        {
            return;
        }
        var after = Collect(property.Value);
        foreach (var finding in call.MayWriteScript ? [] : after.Findings)
        {
            if (before.Take(finding))
            {
                continue;
            }
            throw finding.Link
                ? AgentException.Usage($"'{property.Name}' has {finding.Problem}.",
                    "Give an http, https, mailto or tel link, a relative one, or a content ref, as {href, text, title, target}. A link the property already had may stay as it is. Nothing was saved.")
                : AgentException.Usage($"'{property.Name}' has {finding.Problem}, which could run script in the CMS edit UI or on the site.",
                    "Leave it out: rich text may have text, links, images, tables and embedded blocks, not script. What the property already had may stay as it is, but nothing new. Nothing was saved.");
        }
        if (!call.MayReferenceUnchecked)
        {
            // As the markup names them (the CMS fills in the other form), so content an editor can't read fails exactly as
            // missing content does; what the stored value already named, the editor has seen there.
            foreach (var reference in after.References.Where(r => !before.TakeReference(r)))
            {
                locator.Resolve(reference, $"embedded block in {property.Name}");
            }
        }
    }

    /// <summary>
    /// What a value holds that an editor may not add (<see cref="CheckValue"/>), each as often as it holds it: findings of
    /// <see cref="MarkupSafety"/> in rich text and links, and the content embedded blocks in rich text name.
    /// </summary>
    private sealed class Constructs
    {
        private readonly Dictionary<string, int> _findings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _references = new(StringComparer.Ordinal);

        public List<(string Key, string Problem, bool Link, bool Live)> Findings { get; } = [];

        public List<string> References { get; } = [];

        /// <param name="live">
        /// Whether a browser surely reads it as live (<see cref="MarkupSafety.Finding.Live"/>): only such a construct counts
        /// as one the value has, which the value written back may keep.
        /// </param>
        public void Add(string key, string problem, bool link, bool live = true)
        {
            Findings.Add((key, problem, link, live));
            if (live)
            {
                _findings[key] = _findings.GetValueOrDefault(key) + 1;
            }
        }

        public void AddReference(string reference)
        {
            References.Add(reference);
            _references[reference] = _references.GetValueOrDefault(reference) + 1;
        }

        /// <summary>Uses up one of this construct; false when there is none (left), or the one written isn't live itself.</summary>
        public bool Take((string Key, string Problem, bool Link, bool Live) finding) => finding.Live && Take(_findings, finding.Key);

        public bool TakeReference(string reference) => Take(_references, reference);

        private static bool Take(Dictionary<string, int> counts, string key)
        {
            if (counts.GetValueOrDefault(key) is var count and > 0)
            {
                counts[key] = count - 1;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Whether a list item's text member is a URL: marked as one (<c>[UIHint("Url")]</c>, <c>[DataType(DataType.Url)]</c>,
    /// <c>[Url]</c>), or named like one (<c>...Url</c>, <c>...Href</c>, <c>...Link</c>).
    /// </summary>
    private static bool IsUrl(System.Reflection.PropertyInfo member)
    {
        var attributes = member.GetCustomAttributes(inherit: true);
        return attributes.OfType<System.ComponentModel.DataAnnotations.UIHintAttribute>().Any(a => a.UIHint.Equals("Url", StringComparison.OrdinalIgnoreCase))
            || attributes.OfType<System.ComponentModel.DataAnnotations.DataTypeAttribute>().Any(a => a.DataType == System.ComponentModel.DataAnnotations.DataType.Url)
            || attributes.OfType<System.ComponentModel.DataAnnotations.UrlAttribute>().Any()
            || member.Name.EndsWith("Url", StringComparison.OrdinalIgnoreCase) || member.Name.EndsWith("Href", StringComparison.OrdinalIgnoreCase)
            || member.Name.EndsWith("Link", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The attributes a link may have; the CMS renders every attribute a link holds, an <c>onclick</c> as well.</summary>
    private static readonly HashSet<string> LinkAttributes = new(StringComparer.OrdinalIgnoreCase) { "href", "title", "target" };

    /// <summary>What <paramref name="value"/> holds that an editor may not add; nothing for the developer, who may.</summary>
    private Constructs Collect(object? value)
    {
        var constructs = new Constructs();
        if (!call.MayWriteScript || !call.MayReferenceUnchecked)
        {
            Collect(value, constructs, depth: 0);
        }
        return constructs;
    }

    private static void Collect(object? value, Constructs constructs, int depth)
    {
        switch (value)
        {
            case null or string or ContentArea or IContentData:
                // Blocks are checked property by property as they are written.
                return;
            case XhtmlString html:
                var markup = html.ToInternalString() ?? "";
                foreach (var finding in MarkupSafety.Scripts(markup))
                {
                    constructs.Add(finding.Key, finding.Problem, link: false, finding.Live);
                }
                foreach (var (_, reference) in MarkupSafety.ContentReferences(markup))
                {
                    constructs.AddReference(reference);
                }
                return;
            case LinkItem link:
                // The attributes it has, whether read from markup or set one by one (where an unset one is empty).
                var key = "link " + string.Join(" ", link.Attributes.Where(a => !string.IsNullOrEmpty(a.Value))
                    .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase).Select(a => $"{a.Key.ToLowerInvariant()}=\"{a.Value}\""));
                if (link.Href is { } href && MarkupSafety.Link(href) is { } problem)
                {
                    constructs.Add(key, problem, link: true);
                }
                else if (link.Attributes.FirstOrDefault(a => !LinkAttributes.Contains(a.Key) && !string.IsNullOrEmpty(a.Value)).Key is { } attribute)
                {
                    constructs.Add(key, $"a link with the attribute '{attribute}' (only href, title and target)", link: true);
                }
                return;
            case Url url:
                if (MarkupSafety.Link(url.OriginalString) is { } urlProblem)
                {
                    constructs.Add("url " + url.OriginalString, urlProblem, link: true);
                }
                return;
            case System.Collections.IEnumerable items:
                foreach (var item in items)
                {
                    Collect(item, constructs, depth);
                }
                return;
            default:
                // A list property's item (PropertyList<T>): its links, URLs and rich text, a few levels deep.
                if (depth < 3 && value.GetType() is { IsPrimitive: false, IsEnum: false } type
                    && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true && type.Namespace?.StartsWith("EPiServer", StringComparison.Ordinal) != true)
                {
                    foreach (var member in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Where(m => m.CanRead && m.GetIndexParameters().Length == 0 && !m.PropertyType.IsValueType))
                    {
                        object? memberValue;
                        try
                        {
                            memberValue = member.GetValue(value);
                        }
                        catch (System.Reflection.TargetInvocationException)
                        {
                            continue;
                        }
                        if (memberValue is string text)
                        {
                            if (IsUrl(member) && MarkupSafety.Link(text) is { } textProblem)
                            {
                                constructs.Add($"url {member.Name} {text}", textProblem, link: true);
                            }
                            continue;
                        }
                        Collect(memberValue, constructs, depth + 1);
                    }
                }
                return;
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
                ? Compat.CmsApi.ShortcutLink(targetPage)
                : throw AgentException.Usage($"A shortcut must point at a page; {shortcut.To} is {target.GetOriginalType().Name}.");
        }
        else if (type == PageShortcutType.External)
        {
            if (!call.MayWriteScript && shortcut.Url is { } url && MarkupSafety.Link(url) is { } problem)
            {
                throw AgentException.Usage($"The shortcut has {problem}.",
                    "Give an http, https, mailto or tel link, a relative one, or a page (\"to\"). Nothing was saved.");
            }
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

    /// <summary>
    /// A page type for a PageType property (e.g. a page list's type filter), by name, id or GUID. Only a page type: the
    /// property's model gets it as a <see cref="PageType"/>, which another content type can't be cast to.
    /// </summary>
    private int PageTypeId(string what, JsonElement value)
    {
        var text = (value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString() ?? "").Trim();
        var types = call.Service<IContentTypeRepository>();
        var type = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? types.Load(id)
            : Guid.TryParse(text, out var guid) ? types.Load(guid)
            : types.Load(text) ?? ByNameIgnoringCase(types, text, what);
        if (type is null)
        {
            var names = types.List().OfType<PageType>().Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var suggestion = Suggestions.DidYouMean(text, names);
            throw AgentException.Usage($"No content type '{text}' for {what}, which takes a page type.",
                $"{(suggestion is null ? "" : suggestion + " ")}Page types: {string.Join(", ", names)}.");
        }
        return type is PageType
            ? type.ID
            : throw AgentException.Invalid([new ValidationIssue(what, $"{type.Name} isn't a page type; {what} takes a page type (by name or id).")]);
    }

    /// <summary>
    /// A content type whose name differs from <paramref name="name"/> only in case. CMS 12's lookup by name is
    /// case-sensitive and CMS 13's isn't: this makes both take <c>articlepage</c>.
    /// </summary>
    /// <exception cref="AgentException"><c>usage</c> when several types match.</exception>
    private static ContentType? ByNameIgnoringCase(IContentTypeRepository types, string name, string what)
    {
        var matches = types.List().Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count <= 1
            ? matches.SingleOrDefault()
            : throw AgentException.Usage($"'{name}' for {what} matches {matches.Count} content types: {string.Join(", ", matches.Select(t => t.Name))}.",
                "Give the name in its exact case, or the type's id.");
    }

    /// <summary>Above this, a hint doesn't list every selectable category: a site can have hundreds.</summary>
    private const int MaxListedCategories = 30;

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
                var list = names.Count == 0 ? "The site has no categories (they're made in admin mode)."
                    : names.Count <= MaxListedCategories ? $"Categories: {string.Join(", ", names)}."
                    : call.ForCaller("`opticli categories` lists them.", $"The site has {names.Count} categories to choose from.");
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
                property.Value = BuildArea(value.Deserialize<List<AreaItemValue>>(AgentJson.Options)!, property.Value as ContentArea, property.Name);
                return;
            case JsonValueKind.String or JsonValueKind.Number when !call.MayReferenceUnchecked && (property is PropertyContentArea || IsReferenceList(property)):
                // The stored text form names content without the read check: refs only, as an array.
                throw AgentException.Usage(
                    $"'{property.Name}' takes an array, not text.",
                    property is PropertyContentArea
                        ? "Give its items as an array of refs, e.g. [{\"ref\": \"123\"}], or change it with areaOps."
                        : "Give its content as an array of refs, e.g. [\"123\", \"124\"].");
            case JsonValueKind.Array when property is PropertyLinkCollection:
                property.Value = BuildLinks(value.Deserialize<List<LinkItemValue>>(AgentJson.Options)!);
                return;
            case JsonValueKind.String when !call.MayWriteScript
                && (property is PropertyLinkCollection || (property.PropertyValueType == typeof(LinkItem) && value.GetString()!.TrimStart().StartsWith('<'))):
                // The stored markup keeps every attribute of a link, and the CMS renders them all.
                throw AgentException.Usage(
                    $"'{property.Name}' takes links as objects, not markup.",
                    property is PropertyLinkCollection
                        ? "Give an array of {href, text, title, target}, as get_content shows it."
                        : "Give {href, text, title, target}, or just the href.");
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
            // PropertyPageReference (a page reference, obsolete on CMS 13) is a PropertyContentReference too.
            case JsonValueKind.String when property is PropertyContentReference:
                // Accept GUIDs too, which ParseToSelf doesn't.
                property.ParseToSelf(locator.ResolveContent(value.GetString(), $"reference for {property.Name}").ToString());
                return;
            case JsonValueKind.Number when !call.MayReferenceUnchecked && property is PropertyContentReference:
                property.ParseToSelf(locator.ResolveContent(value.GetRawText(), $"reference for {property.Name}").ToString());
                return;
            case JsonValueKind.String or JsonValueKind.Number when property is PropertyPageType && !(value.ValueKind == JsonValueKind.String && value.GetString() is "" or null):
                // By name (as get shows it), id or GUID; ParseToSelf takes an id only, of any content type. "" still
                // clears it through ParseToSelf below, as it always did.
                property.Value = PageTypeId(property.Name, value);
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

    /// <summary>A list of content references (<c>IList&lt;ContentReference&gt;</c>).</summary>
    private static bool IsReferenceList(PropertyData property) =>
        property.PropertyValueType != typeof(ContentReference) && typeof(IEnumerable<ContentReference>).IsAssignableFrom(property.PropertyValueType);

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

        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out _) && value.EnumerateObject().All(p => p.Name is "type" or "value" or "blockType"))
        {
            throw AgentException.Usage($"'{property.Name}' was given get_content's {{type, value}} shape.",
                "Give the value alone: for a block, an object of its property names to their values.");
        }
        // Interfaces like IList<string> can't be instantiated; a List<T> satisfies them.
        var target = element is not null && type.IsInterface ? typeof(List<>).MakeGenericType(element) : type;
        try
        {
            return value.Deserialize(target, AgentJson.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            // The type isn't one JSON can make (a URL, a CMS type): no fault of the site's.
            throw AgentException.Usage($"Can't set '{property.Name}' ({property.GetType().Name}) from this JSON: {ex.Message}",
                "Give a string for a URL or a link, or see get_content_type for what the property takes.");
        }
    }

    /// <summary>
    /// A block list: one new block per object, with the object's values set like a local block's. For an editor, each new
    /// block keeps what the item at its index had that they can't see or change (it would otherwise be lost unseen), and
    /// a shorter list is refused when an item it drops has such a value.
    /// </summary>
    private System.Collections.IList BlockList(PropertyData property, Type element, JsonElement value)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        var current = (property.Value as System.Collections.IEnumerable)?.OfType<IContentData>().ToList() ?? [];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw AgentException.Usage($"{property.Name}[{index}] must be an object of {element.Name} property names to values, e.g. {{\"Name\": \"...\"}}.");
            }
            var block = blocks.Create(element);
            var replaced = index < current.Count && current[index].GetOriginalType() == block.GetOriginalType() ? current[index] : null;
            if (replaced is not null)
            {
                _blockBaselines[block] = replaced;
            }
            Apply(block, item.Deserialize<Dictionary<string, JsonElement>>(AgentJson.Options));
            foreach (var kept in replaced is null ? [] : Unseen(replaced))
            {
                block.Property[kept.Name].Value = kept.Value;
            }
            list.Add(block);
            index++;
        }
        if (current.Skip(index).FirstOrDefault(dropped => Unseen(dropped).Any(p => !p.IsNull)) is not null)
        {
            throw AgentException.Usage($"{property.Name} would lose items with values you can't see or change in the CMS edit UI.",
                "Keep as many items as it has, or ask someone who may change those values to edit the list in the CMS. Nothing was saved.");
        }
        return list;
    }

    /// <summary>The properties of <paramref name="block"/> the caller can't see or change: none for the developer.</summary>
    private IEnumerable<PropertyData> Unseen(IContentData block) =>
        block.Property.Where(p => !p.IsMetaData && call.Properties.Access(block, p) != PropertyAccess.Editable).ToList();

    /// <summary>
    /// The area of <paramref name="items"/>, replacing <paramref name="current"/>. Each item takes over the current item
    /// that <see cref="AreaItemRules.Match"/> pairs it with (the same content, or an inline block of the same type): its
    /// group and visitor groups unless the item gives them, and its other render settings; an inline block also its
    /// values, the item's being set on a copy of it. Without that, writing back what <c>get</c> shows would lose the
    /// area's personalization, and an inline block's values the item leaves out.
    /// </summary>
    /// <param name="name">The ContentArea property, for messages.</param>
    public ContentArea BuildArea(IReadOnlyList<AreaItemValue> items, ContentArea? current, string name)
    {
        var wanted = items.Select((item, i) => Wanted(item, $"{name}[{i}]")).ToList();
        var currentItems = current?.Items.ToList() ?? [];
        var matches = AreaItemRules.Match(currentItems.Select(Key).ToList(), wanted.Select(w => w.Key).ToList(),
            (i, j) => wanted[i].InlineType is not null && Unchanged(currentItems[j], items[i]));

        var area = new ContentArea();
        for (var i = 0; i < items.Count; i++)
        {
            var takenOver = matches[i] < 0 ? null : currentItems[matches[i]];
            var item = wanted[i].InlineType is { } type
                ? NewInlineItem(type, items[i].Properties, items[i].Name, items[i].DisplayOption, takenOver, $"{name}[{i}]")
                : NewAreaItem(wanted[i].Link!, items[i].DisplayOption, takenOver);
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

    /// <summary>
    /// Whether <paramref name="item"/> gives <paramref name="current"/>'s inline block as it is: every value it has (that
    /// the caller sees), and those values set on a copy of it change nothing. Its name, display option and personalization
    /// don't count; they are as given either way.
    /// </summary>
    private bool Unchanged(ContentAreaItem current, AreaItemValue item)
    {
        if (InlineBlocks.Of(current) is not { } block)
        {
            return false;
        }
        var before = PropertyValues.Snapshot(block);
        var given = item.Properties ?? new Dictionary<string, JsonElement>();
        // What an editor doesn't see, they can't give back.
        if (before.Any(p => p.Value is not null && !given.Keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)
            && (block.Property[p.Key] is not { } property || call.Properties.Shown(block, property))))
        {
            return false;
        }
        var copy = (BlockData)((EPiServer.Data.Entity.IReadOnly)block).CreateWritableClone();
        try
        {
            Apply(copy, given);
        }
        catch (AgentException)
        {
            // Values it can't take: the write reports them for the item it pairs with.
            return false;
        }
        var after = PropertyValues.Snapshot(copy);
        return before.All(p => after.GetValueOrDefault(p.Key)?.GetRawText() == p.Value?.GetRawText());
    }

    /// <summary>What an item of a whole area shows: content (its link), or a new inline block (its type).</summary>
    /// <exception cref="AgentException"><c>usage</c> for an item that gives both, or neither.</exception>
    private (ContentReference? Link, ContentType? InlineType, string? Key) Wanted(AreaItemValue item, string where)
    {
        var shared = item.Ref is not null || item.Guid is not null;
        var inline = item.Type is not null || item.Inline == true || item.Properties is not null || item.Name is not null;
        if (shared && inline)
        {
            throw AgentException.Usage($"{where} gives both a shared block (ref or guid) and an inline block (type, properties, name or inline).",
                "An item is one or the other: {\"ref\": \"123\"} for a shared block, {\"type\": \"TeaserBlock\", \"properties\": {...}} for an inline one.");
        }
        if (inline)
        {
            var type = InlineType(item.Type ?? throw AgentException.Usage($"{where} is an inline block without its type.",
                "Give its block type: {\"type\": \"TeaserBlock\", \"properties\": {...}}."), where);
            return (null, type, AreaItemRules.InlineKey(type.ID));
        }
        if (!shared)
        {
            throw AgentException.Usage($"{where} names no block.",
                "Give {\"ref\": \"123\"} (or guid) for a shared block, or {\"type\": \"TeaserBlock\", \"properties\": {...}} for an inline one.");
        }
        var link = Target(item.Guid?.ToString() ?? item.Ref);
        return (link, null, link.ToString());
    }

    /// <summary>The key a current item is paired by (<see cref="AreaItemRules.Match"/>): its content, or its inline block's type.</summary>
    private string? Key(ContentAreaItem item) =>
        InlineBlocks.Of(item) is { } block ? AreaItemRules.InlineKey(BlockTypeId(block))
        : ContentReference.IsNullOrEmpty(item.ContentLink) ? null : item.ContentLink.ToReferenceWithoutVersion().ToString();

    private int BlockTypeId(BlockData block) =>
        InlineBlocks.TypeId(block) is var id and > 0 ? id : call.Service<IContentTypeRepository>().Load(block.GetOriginalType())?.ID ?? 0;

    /// <summary>The block type of a new inline block, by name (any case), id or GUID.</summary>
    /// <exception cref="AgentException"><c>usage</c> for a type that isn't a block type; <c>not_found</c> for no such type.</exception>
    private ContentType InlineType(string name, string where)
    {
        InlineBlocks.Require();
        var types = call.Service<IContentTypeRepository>();
        var type = int.TryParse(name.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? types.Load(id) ?? throw AgentException.NotFound($"{where}: no content type has the id {id}.")
            : Operations.TypeOperation.Find(call, types, name);
        if (!CmsApi.IsBlockType(type) || type.ModelType is { } model && !typeof(BlockData).IsAssignableFrom(model))
        {
            throw AgentException.Usage($"{where}: {type.Name} isn't a block type, so it can't be an inline block.",
                call.ForCaller("`opticli types --kind block` lists the block types; `opticli allowed-in <type>` says which areas take one.", "get_content_type on the content's type lists what its ContentAreas take."));
        }
        return type;
    }

    /// <summary>The name of the visitor group with this id; null when there is none (or the site has no personalization).</summary>
    private static string? VisitorGroupName(Guid id) =>
        ServiceLocator.Current.TryGetExistingInstance(out EPiServer.Personalization.VisitorGroups.IVisitorGroupRepository? groups) && groups is not null ? groups.Load(id)?.Name : null;

    public ContentAreaItem NewAreaItem(string? reference, string? displayOption) => NewAreaItem(Target(reference), displayOption, null);

    /// <summary>A new item with a new inline block of <paramref name="type"/> (name, id or GUID), with <paramref name="values"/> set.</summary>
    public ContentAreaItem NewInlineItem(string type, IReadOnlyDictionary<string, JsonElement>? values, string? name, string? displayOption, string where) =>
        NewInlineItem(InlineType(type, where), values, name, displayOption, null, where);

    /// <summary>
    /// Whether <paramref name="items"/> has an inline block like the one <paramref name="added"/> shows: of its type, with
    /// the values of <paramref name="given"/> (as the new block has them) and the name, if one is given.
    /// </summary>
    public bool HasInlineLike(IEnumerable<ContentAreaItem> items, ContentAreaItem added, IReadOnlyDictionary<string, JsonElement>? given, string? name)
    {
        var block = InlineBlocks.Of(added)!;
        var wanted = PropertyValues.Snapshot(block);
        var typeId = BlockTypeId(block);
        return items.Any(item => InlineBlocks.Of(item) is { } existing && BlockTypeId(existing) == typeId
            && (name is null || string.Equals(InlineBlocks.Name(item) ?? "", name.Trim(), StringComparison.Ordinal))
            && PropertyValues.Snapshot(existing) is var has
            && (given?.Keys ?? []).All(key => has.GetValueOrDefault(key)?.GetRawText() == wanted.GetValueOrDefault(key)?.GetRawText()));
    }

    /// <summary>The content an item shows, without version.</summary>
    private ContentReference Target(string? reference) =>
        locator.LoadAnyLanguage(locator.ResolveContent(reference, "ContentArea item")).ContentLink.ToReferenceWithoutVersion();

    /// <param name="takenOver">The current item this one replaces: its render settings other than the display option are kept.</param>
    private ContentAreaItem NewAreaItem(ContentReference link, string? displayOption, ContentAreaItem? takenOver)
    {
        // Link only: on newer CMS versions setting ContentGuid clears ContentLink (they are alternatives).
        var item = new ContentAreaItem { ContentLink = link };
        Settings(item, displayOption, takenOver);
        return item;
    }

    /// <summary>
    /// An item with an inline block of <paramref name="type"/>: a copy of <paramref name="takenOver"/>'s, else a new one as
    /// the CMS makes it (with the type's default values), and <paramref name="values"/> set on it.
    /// </summary>
    /// <param name="name">The name in the area, as given (like the display option): null or <c>""</c> for none.</param>
    private ContentAreaItem NewInlineItem(ContentType type, IReadOnlyDictionary<string, JsonElement>? values, string? name, string? displayOption, ContentAreaItem? takenOver, string where)
    {
        BlockData block;
        if (takenOver is not null && InlineBlocks.Of(takenOver) is { } current)
        {
            block = (BlockData)((EPiServer.Data.Entity.IReadOnly)current).CreateWritableClone();
        }
        else
        {
            // An editor gets the types the edit UI offers for a new block of the content (its "For this page" folder's).
            if (call.Service<IContentTypeRepository>().Load(typeof(ContentAssetFolder)) is { } assets && !call.MayCreate(type, assets))
            {
                throw AgentException.Usage($"{where}: you may not create {type.Name} blocks: the content type's access rights, or those of its group of types, don't allow it.");
            }
            block = blocks.Create(type);
            NewInlineBlocks++;
        }
        ApplyTo(block, values, where);
        var item = new ContentAreaItem();
        InlineBlocks.Set(item, block, type.ID);
        Settings(item, displayOption, takenOver, inlineName: name ?? "");
        return item;
    }

    /// <summary>
    /// An item's render settings: those of <paramref name="takenOver"/> but the display option (and an inline block's
    /// name), then the display option (and name) given.
    /// </summary>
    /// <param name="inlineName">An inline block's name, as given (empty for none); null for an item that shows content.</param>
    private void Settings(ContentAreaItem item, string? displayOption, ContentAreaItem? takenOver, string? inlineName = null)
    {
        foreach (var (key, value) in CmsApi.RenderSettings(takenOver))
        {
            if (key != PropertyValues.DisplayOptionKey && !(inlineName is not null && key == InlineBlocks.NameKey))
            {
                CmsApi.SetRenderSetting(item, key, value);
            }
        }
        if (!string.IsNullOrWhiteSpace(inlineName))
        {
            CmsApi.SetRenderSetting(item, InlineBlocks.NameKey, inlineName.Trim());
        }
        if (DisplayOptionId(displayOption, takenOver is null ? null : PropertyValues.DisplayOption(takenOver)) is { } id)
        {
            CmsApi.SetRenderSetting(item, PropertyValues.DisplayOptionKey, id);
        }
    }

    /// <summary>How many new inline blocks (not copies of current ones) the writes made.</summary>
    public int NewInlineBlocks { get; private set; }

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
