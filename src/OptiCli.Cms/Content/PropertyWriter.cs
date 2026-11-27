using System.Globalization;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Filters;
using EPiServer.SpecializedProperties;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using EPiServer.Web.Routing;
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
/// <param name="sharedBlocks">
/// The site's edit UI makes new blocks in a ContentArea shared, in the content's "For this page" folder, rather than inline
/// (<see cref="InlineBlocks.EditUiCreatesThem"/> is false): a new block in an area is then made so too
/// (<see cref="Shared"/>). Inline blocks an area has already are still changed, moved and removed as they are.
/// </param>
internal sealed class PropertyWriter(
    CmsCall call, ContentLocator locator, BlockFactory blocks, CategoryRepository categories, IFrameRepository frames, DisplayOptions displayOptions,
    bool sharedBlocks = false)
{
    /// <summary>
    /// Where the site's edit UI makes new blocks in a ContentArea shared: the new blocks the writes record, to be saved
    /// with the content (<see cref="SharedAreaBlocks"/>). Null where it makes them inline, or can't be asked (as before).
    /// </summary>
    public SharedAreaBlocks? Shared { get; } = sharedBlocks ? new SharedAreaBlocks(call, locator) : null;

    /// <summary>The content the writes are for: the first one written (<see cref="Apply"/>, <see cref="FindArea"/>), whose folder new shared blocks go in.</summary>
    private void Own(IContentData content)
    {
        if (Shared is { Owner: null } && content is IContent owner)
        {
            Shared.Owner = owner;
        }
    }

    /// <summary>Pseudo-property for the content name in snapshots and diffs.</summary>
    public const string NameKey = "Name";

    /// <summary>
    /// For an editor: each block of a block list being written, with the item it replaces (the one at its index, of its
    /// type). What that item had is what the new one may keep (<see cref="CheckValue"/>), and its values the editor can't
    /// see or change are carried over (<see cref="BlockList"/>).
    /// </summary>
    private readonly Dictionary<IContentData, IContentData> _blockBaselines = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The ContentAreas of an inline block rebuilt for a changed item (<see cref="BuildArea"/>), with the area of the block
    /// it replaces: a whole area given for them pairs its items with that block's items, as it would on the block itself.
    /// </summary>
    private readonly Dictionary<PropertyData, ContentArea?> _areaBaselines = new(ReferenceEqualityComparer.Instance);

    /// <summary>Above 0 while a copy of a block is tried (<see cref="Unchanged"/>): nothing of it counts or warns.</summary>
    private int _trial;

    /// <summary>Where the values being set are, for messages: <c>MainArea[0].</c> inside an inline block.</summary>
    private string _prefix = "";

    /// <summary>What the writes noticed that the caller should know but that doesn't stop them.</summary>
    public List<ValidationIssue> Warnings { get; } = [];

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
        Own(content);
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
    /// The ContentArea <paramref name="path"/> names, for area edits: a property of the content (<c>MainArea</c>), or of a
    /// block in it: an inline block by its position (<c>MainArea[0].Area</c>), a local block by name (<c>Hero.Area</c>),
    /// at any depth. Each property on the way is one the caller may change, and the first one must be culture-specific
    /// outside the master language, as for <c>set</c>.
    /// </summary>
    /// <param name="commit">
    /// Puts the copies of the areas on the way back (their inline blocks hold the area changed), after the area itself is set.
    /// </param>
    /// <exception cref="AgentException"><c>usage</c> for a path that doesn't lead to a ContentArea.</exception>
    public PropertyData FindArea(IContentData content, string path, out Action commit)
    {
        Own(content);
        var segments = path.Split('.');
        var owner = content;
        var shown = "";
        var commits = new List<Action>();
        for (var s = 0; s < segments.Length; s++)
        {
            var position = AreaItemRules.Indexed(segments[s]);
            var property = Find(owner, position?.Property ?? segments[s].Trim());
            if (s == 0)
            {
                RequireLanguage(content, property);
            }
            shown = s == 0 ? property.Name : $"{shown}.{property.Name}";
            if (s == segments.Length - 1)
            {
                if (position is not null || property is not PropertyContentArea)
                {
                    throw AgentException.Usage($"'{path}' names {(position is not null ? "an item of a ContentArea" : $"a {property.GetType().Name}")}, not a ContentArea.",
                        "Name the area: MainArea, or one inside a block, e.g. MainArea[0].Area or Hero.Area.");
                }
                commit = () =>
                {
                    for (var c = commits.Count - 1; c >= 0; c--)
                    {
                        commits[c]();
                    }
                };
                return property;
            }
            if (position is { } item)
            {
                if (property is not PropertyContentArea)
                {
                    throw AgentException.Usage($"'{shown}[{item.Index}]' names an item of a ContentArea, and '{shown}' is a {property.GetType().Name}.");
                }
                var area = property.Value is ContentArea existing ? (ContentArea)((EPiServer.Data.Entity.IReadOnly)existing).CreateWritableClone() : new ContentArea();
                if (item.Index >= area.Items.Count)
                {
                    throw AgentException.Usage($"'{shown}' has {area.Items.Count} item(s), so there is no {shown}[{item.Index}] (positions are zero-based).");
                }
                var target = area.Items[item.Index];
                if (Shared?.Of(target) is { } pending)
                {
                    // A block this write makes: its areas are set on its values, which are saved with it.
                    var pendingHolder = property;
                    commits.Add(() => pendingHolder.Value = area);
                    owner = pending.Values;
                    shown += $"[{item.Index}]";
                    continue;
                }
                if (InlineBlocks.Of(target) is not { } block)
                {
                    var link = ContentReference.IsNullOrEmpty(target.ContentLink) ? null : target.ContentLink.ToReferenceWithoutVersion().ToString();
                    throw AgentException.Usage($"{shown}[{item.Index}] is {(link is null ? "an item without content" : $"the shared block {link}")}, not an inline block, so it has no areas of its own here.",
                        link is null ? null : call.ForCaller($"Change the shared block itself: opticli area {link} <area> ..., or opticli set {link} Area=[...].", $"Change the shared block itself: update_content on {link}."));
                }
                if (block is EPiServer.Data.Entity.IReadOnly { IsReadOnly: true } readOnly)
                {
                    block = (BlockData)readOnly.CreateWritableClone();
                    InlineBlocks.Set(target, block, InlineBlocks.TypeId(block));
                }
                var holder = property;
                commits.Add(() => holder.Value = area);
                owner = block;
                shown += $"[{item.Index}]";
            }
            else if (property.Value is BlockData local and not IContent)
            {
                owner = local;
            }
            else
            {
                throw AgentException.Usage($"'{shown}' is neither a ContentArea item ({shown}[n]) nor a local block, so '{path}' leads nowhere.");
            }
        }
        throw new InvalidOperationException("A path has at least one segment.");
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
        var stored = property.Value as ContentArea ?? (_areaBaselines.TryGetValue(property, out var baseline) ? baseline : null);
        var area = stored is not null ? (ContentArea)((EPiServer.Data.Entity.IReadOnly)stored).CreateWritableClone() : new ContentArea();
        ChangeInlineItem(area, _prefix + property.Name, index, value.Deserialize<Dictionary<string, JsonElement>>(AgentJson.Options), null);
        property.Value = area;
    }

    /// <summary>
    /// Sets <paramref name="values"/> (and the name, when given; <c>""</c> removes it) on the inline block at
    /// <paramref name="index"/> of <paramref name="area"/>, a writable one; its other values and the item's other render
    /// settings and personalization stay as they are.
    /// </summary>
    /// <exception cref="AgentException"><c>usage</c> for no such item, or one that isn't an inline block.</exception>
    public void ChangeInlineItem(ContentArea area, string property, int index, IReadOnlyDictionary<string, JsonElement>? values, string? name)
    {
        var path = $"{property}[{index}]";
        var items = area.Items;
        if (index < 0 || index >= items.Count)
        {
            throw AgentException.Usage($"'{property}' has {items.Count} item(s), so there is no {path} (positions are zero-based).",
                call.ForCaller("Read the positions in the version you change: opticli get <ref> --version latest --fields " + property + ".", "Read the positions with get_content (version latest)."));
        }
        var item = items[index];
        if (Shared?.Of(item) is { } pending)
        {
            // A block this write makes (an earlier add): its values and name, as for an inline block.
            ApplyTo(pending.Values, values, path);
            if (name is not null)
            {
                pending.GivenName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            return;
        }
        if (InlineBlocks.Of(item) is not { } block)
        {
            var shown = ContentReference.IsNullOrEmpty(item.ContentLink) ? null : item.ContentLink.ToReferenceWithoutVersion().ToString();
            throw AgentException.Usage($"{path} is {(shown is null ? "an item without content" : $"the shared block {shown}")}, not an inline block, so it has no values of its own in the area.",
                shown is null ? null : call.ForCaller($"Change the block itself: opticli set {shown} Prop=value.", $"Change the block itself: update_content on {shown}."));
        }
        if (block is EPiServer.Data.Entity.IReadOnly { IsReadOnly: true } readOnly)
        {
            block = (BlockData)readOnly.CreateWritableClone();
            InlineBlocks.Set(item, block, InlineBlocks.TypeId(block));
        }
        ApplyTo(block, values, path);
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                item.RenderSettings?.Remove(InlineBlocks.NameKey);
            }
            else
            {
                CmsApi.SetRenderSetting(item, InlineBlocks.NameKey, name.Trim());
            }
        }
    }

    /// <summary>Sets values on a block in a ContentArea, with errors saying which item it is.</summary>
    private void ApplyTo(BlockData block, IReadOnlyDictionary<string, JsonElement>? values, string where)
    {
        var outer = _prefix;
        _prefix = $"{where}.";
        try
        {
            Apply(block, values);
        }
        catch (AgentException ex) when (ex.Code is AgentErrorCodes.Usage or AgentErrorCodes.NotFound && !ex.Message.StartsWith(where, StringComparison.Ordinal))
        {
            throw new AgentException(ex.Code, $"{where}: {ex.Message}", ex.Hint) { Validation = ex.Validation, Reason = ex.Reason };
        }
        finally
        {
            _prefix = outer;
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
            throw finding.Key.StartsWith(UncheckedKey, StringComparison.Ordinal)
                ? AgentException.Usage($"'{property.Name}' has {finding.Problem}, which can't be checked for an editor.",
                    "Leave it out, or ask the user to change it in the CMS edit UI. Nothing was saved.")
                : finding.Link
                ? AgentException.Usage($"'{property.Name}' has {finding.Problem}.",
                    "Give an http, https, mailto or tel link, a relative one, or a content ref, as {href, text, title, target}. A link the property already had may stay as it is. Nothing was saved.")
                : AgentException.Usage($"'{property.Name}' has {finding.Problem}, which could run script in the CMS edit UI or on the site.",
                    "Leave it out: rich text may have text, links, images, tables and embedded blocks, not script. What the property already had may stay as it is, but nothing new. Nothing was saved.");
        }
        if (!call.MayReferenceUnchecked)
        {
            // As the markup names them (the CMS fills in the other form), and the content references the value holds, so
            // content an editor can't read fails exactly as missing content does; what the stored value already named, the
            // editor has seen there.
            foreach (var reference in after.References.Where(r => !before.TakeReference(r)))
            {
                // A version must be one of that content's own: the CMS loads another content's version id as that content.
                if (locator.Resolve(reference, $"content named in {property.Name}") is { WorkID: > 0 } version)
                {
                    locator.Version(version, version.WorkID);
                }
            }
        }
    }

    /// <summary>
    /// What a value holds that an editor may not add (<see cref="CheckValue"/>), each as often as it holds it: findings of
    /// <see cref="MarkupSafety"/> in rich text and links, and the content it names: embedded blocks in rich text, and content
    /// references (also in a list property's items).
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

    /// <summary>How deep a list property's item is read (<see cref="Collect(object?, Constructs, int)"/>).</summary>
    private const int MaxDepth = 6;

    /// <summary>The key prefix of a finding that isn't script but a value the check can't read: refused all the same.</summary>
    private const string UncheckedKey = "unchecked ";

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
            case ContentReference reference:
                // However it was given (an object, a list item's member): the read check, as for an embedded block. Not
                // for ids that name no content (SelfReference and the like).
                if (reference.ID > 0 && reference.ProviderName is { Length: > 0 } provider && reference.WorkID > 0)
                {
                    // A provider's content version, which a ref can't name: refused unless the value had it already.
                    constructs.Add($"{UncheckedKey}provider version {reference}", "a version of a content provider's content", link: false);
                }
                else if (reference.ID > 0)
                {
                    constructs.AddReference(reference.ProviderName is { Length: > 0 } named ? $"{reference.ID}__{named}"
                        : reference.WorkID > 0 ? $"{reference.ID}_{reference.WorkID}"
                        : reference.ID.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                return;
            case ContentAreaItem item:
                // In a list property's item; a ContentArea property itself is checked as it is built (BuildArea).
                Collect(item.ContentLink, constructs, depth);
                return;
            case System.Collections.IDictionary dictionary:
                // Its values; the keys are text.
                foreach (var item in dictionary.Values)
                {
                    Collect(item, constructs, depth);
                }
                return;
            case System.Collections.IEnumerable items:
                foreach (var item in items)
                {
                    Collect(item, constructs, depth);
                }
                return;
            default:
                if (value.GetType() is { IsGenericType: true } pair && pair.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                {
                    Collect(pair.GetProperty(nameof(KeyValuePair<int, int>.Value))!.GetValue(value), constructs, depth);
                    return;
                }
                // A list property's item (PropertyList<T>): its links, URLs, rich text and references, a few levels deep.
                if (value.GetType() is { IsPrimitive: false, IsEnum: false } type
                    && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true && type.Namespace?.StartsWith("EPiServer", StringComparison.Ordinal) != true)
                {
                    if (depth >= MaxDepth)
                    {
                        // Deeper than is read: refused rather than let through unseen, and never one the value already had.
                        constructs.Add($"{UncheckedKey}deep {Guid.NewGuid():N}", $"a value nested more than {MaxDepth} levels deep", link: false);
                        return;
                    }
                    foreach (var member in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Where(m => m.CanRead && m.GetIndexParameters().Length == 0 && !m.PropertyType.IsPrimitive && !m.PropertyType.IsEnum))
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
        var current = which == StartPublishKey ? versionable.StartPublish : versionable.StopPublish;
        if (SameMillisecond(current, parsed))
        {
            // get shows dates to the millisecond (all CMS 12's datetime holds); CMS 13 keeps ticks. Written back as get
            // shows it, a date is the one the content has, not a change.
            return;
        }
        if (which == StartPublishKey)
        {
            versionable.StartPublish = parsed;
        }
        else
        {
            versionable.StopPublish = parsed;
        }
    }

    /// <summary>Whether two times are the same instant to within a millisecond (both null counts).</summary>
    internal static bool SameMillisecond(DateTime? a, DateTime? b) =>
        a is null || b is null
            ? a is null && b is null
            : Math.Abs((a.Value.ToUniversalTime() - b.Value.ToUniversalTime()).Ticks) < TimeSpan.TicksPerMillisecond;

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
        // A property type an extension of the agent knows (PropertyValueConverters).
        if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && PropertyValueConverters.For(property) is { } converter)
        {
            property.Value = converter.FromJson(property, value, reference => locator.ResolveContent(reference, $"content named in {_prefix}{property.Name}"));
            return;
        }
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
                var currentArea = _areaBaselines.TryGetValue(property, out var baseline) ? baseline : property.Value as ContentArea;
                property.Value = BuildArea(value.Deserialize<List<AreaItemValue>>(AgentJson.Options)!, currentArea, property.Name);
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
                var outer = _prefix;
                _prefix = $"{outer}{property.Name}.";
                try
                {
                    Apply(block, value.Deserialize<Dictionary<string, JsonElement>>(AgentJson.Options));
                }
                finally
                {
                    _prefix = outer;
                }
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
            case JsonValueKind.String when StoredAlready(property.Value, value.GetString()!):
                // What the CMS stores for it already, in another spelling: left as it is, so writing it again (a plan
                // run again with --update-existing) changes nothing.
                return;
            case JsonValueKind.String:
                ParseText(property, value.GetString()!);
                return;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                ParseText(property, value.GetRawText());
                return;
            default:
                var structured = Structured(property, value);
                // A list of the site's own items (PropertyList<T>), or another object of its own, that is the current
                // value with its times written otherwise (the CMS reads them back in the server's time zone): left as it
                // is, so writing it again (a plan run again with --update-existing) changes nothing.
                if (structured is not (IEnumerable<IContentData> or IEnumerable<ContentReference>) && SameStructured(property.Value, structured))
                {
                    return;
                }
                property.Value = structured;
                return;
        }
    }

    /// <summary>
    /// Whether <paramref name="given"/> is <paramref name="current"/> as JSON, where times count as the same when they
    /// are the same instant (<c>1900-01-01T08:00:00Z</c> and <c>1900-01-01T09:00:00+01:00</c>; one without an offset is
    /// local, as the CMS reads it). The CMS stores a list's items as JSON and reads their times back as local times,
    /// whatever the value written had, so a value compared as written would always differ.
    /// </summary>
    internal static bool SameStructured(object? current, object? given)
    {
        if (current is null || given is null)
        {
            return false;
        }
        try
        {
            return SameJson(JsonSerializer.SerializeToElement(current, AgentJson.Options), JsonSerializer.SerializeToElement(given, AgentJson.Options));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException)
        {
            // A value JSON can't show: compared as different, so it is written as before.
            return false;
        }
    }

    private static bool SameJson(JsonElement a, JsonElement b)
    {
        switch (a.ValueKind, b.ValueKind)
        {
            case (JsonValueKind.Object, JsonValueKind.Object):
                var left = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var right = b.EnumerateObject().ToList();
                return left.Count == right.Count && right.All(p => left.TryGetValue(p.Name, out var other) && SameJson(other, p.Value));
            case (JsonValueKind.Array, JsonValueKind.Array):
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => SameJson(pair.First, pair.Second));
            case (JsonValueKind.String, JsonValueKind.String):
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal) || SameInstant(a.GetString()!, b.GetString()!);
            default:
                return a.ValueKind == b.ValueKind && a.GetRawText() == b.GetRawText();
        }
    }

    /// <summary>Two ISO 8601 times (as JSON writes a DateTime) of the same instant; one without an offset is local.</summary>
    private static bool SameInstant(string a, string b) =>
        IsoTime(a) is { } x && IsoTime(b) is { } y && x.UtcDateTime == y.UtcDateTime;

    private static DateTimeOffset? IsoTime(string text) =>
        text.Length >= 19 && char.IsAsciiDigit(text[0]) && text[4] == '-' && text[7] == '-' && text[10] == 'T'
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time) ? time : null;

    /// <summary>
    /// Text for a property, parsed by the property itself (<c>ParseToSelf</c>). A property type that can't be set from text
    /// (a <c>PropertyList&lt;T&gt;</c> whose <c>ParseItem</c> the site's or an add-on's type leaves unimplemented, as some
    /// of Optimizely Forms' lists do) is a <c>usage</c> error that says to give the structured value, not a crash.
    /// </summary>
    private void ParseText(PropertyData property, string text)
    {
        try
        {
            property.ParseToSelf(text);
        }
        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
        {
            var list = property.PropertyValueType is { IsGenericType: true } type && type != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
            throw AgentException.Usage(
                $"'{_prefix}{property.Name}' ({property.GetType().Name}) can't be set from text: {ex.Message}",
                list
                    ? $"Give it as a JSON array of its items, as {call.ForCaller("opticli get", "get_content")} shows it, not as a string (in a plan, the value itself, not JSON text in a string)."
                    : $"Give its structured value, as {call.ForCaller("opticli get", "get_content")} shows it, not a string.");
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
            throw AgentException.Usage($"'{property.Name}' was given {call.ForCaller("get", "get_content")}'s {{type, value}} shape.",
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
    /// A block list: one new block per object, with the object's values set like a local block's. Each new block keeps what
    /// the item of its type at its index had that the edit UI shows no one, unless the object names it (the developer), or
    /// that the editor can't see or change (it would otherwise be lost unseen). For an editor, a shorter list is refused
    /// when an item it drops has such a value.
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
            var given = item.Deserialize<Dictionary<string, JsonElement>>(AgentJson.Options)!;
            Apply(block, given);
            foreach (var kept in replaced is null ? [] : call.Properties.Checks ? Unseen(replaced) : HiddenNotGiven(replaced, given.Keys))
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

    /// <summary>
    /// For the developer: the properties of <paramref name="block"/> the edit UI shows no one (a key the site's
    /// <c>SetDefaultValues</c> makes) that <paramref name="given"/> doesn't name. The item that replaces it keeps them, as an
    /// edit in the CMS does; without that, such a value would be new on every write, and a plan never <c>unchanged</c>.
    /// </summary>
    private static IEnumerable<PropertyData> HiddenNotGiven(IContentData block, IEnumerable<string> given)
    {
        var named = new HashSet<string>(given, StringComparer.OrdinalIgnoreCase);
        return block.Property.Where(p => !p.IsMetaData && !named.Contains(p.Name) && EditUiProperties.HiddenFromEveryone(block, p)).ToList();
    }

    /// <summary>The properties of <paramref name="block"/> the caller can't see or change: none for the developer.</summary>
    private IEnumerable<PropertyData> Unseen(IContentData block) =>
        block.Property.Where(p => !p.IsMetaData && call.Properties.Access(block, p) != PropertyAccess.Editable).ToList();

    /// <summary>
    /// The area of <paramref name="items"/>, replacing <paramref name="current"/>. Each item takes over the current item
    /// that <see cref="AreaItemRules.Pair"/> pairs it with: its group and visitor groups unless the item gives them, and its
    /// other render settings. Without that, writing back what <c>get</c> shows would lose the area's personalization. An
    /// inline block keeps its values only when the item is an exact copy of it; any other inline item is a new block with
    /// just the values it gives, as a whole area replaces the area (one block's values change with <c>MainArea[2]</c>). A
    /// changed item that takes over a block is rebuilt with that block as its reference: a ContentArea given in it (also in
    /// its local blocks) pairs with that block's area.
    /// </summary>
    /// <param name="name">The ContentArea property, for messages.</param>
    /// <exception cref="AgentException">
    /// <c>usage</c> for an editor when an inline block the area has now (or one inside it) has values they can't see or
    /// change that would be lost (<see cref="RequireNothingLostUnseen"/>).
    /// </exception>
    public ContentArea BuildArea(IReadOnlyList<AreaItemValue> items, ContentArea? current, string name)
    {
        var path = _prefix + name;
        var wanted = items.Select((item, i) => Wanted(item, $"{path}[{i}]")).ToList();
        var currentItems = current?.Items.ToList() ?? [];
        var pairs = AreaItemRules.Pair(currentItems.Select(Key).ToList(), wanted.Select(w => w.Key).ToList(),
            (i, j) => wanted[i].InlineType is not null && Unchanged(InlineBlocks.Of(currentItems[j]), items[i]),
            (i, j) => wanted[i].InlineType is not null && Alike(items[i], currentItems[j]));
        // Where new blocks are shared: more pairings, with inline blocks (kept inline) and with the folder's own blocks.
        var owned = TakeOverOwnBlocks(currentItems, pairs, wanted.Select(w => w.InlineType).ToList(), items);
        RequireNothingLostUnseen(currentItems, pairs, items, path);
        RequireNoOwnBlockLostUnseen(owned, items, path);
        WarnSettingsDropped(currentItems, pairs, wanted.Select(w => w.Key).ToList(), path);

        var area = new ContentArea();
        for (var i = 0; i < items.Count; i++)
        {
            var takenOver = pairs[i].Current >= 0 ? currentItems[pairs[i].Current] : owned[i] is { } ownItem ? currentItems[ownItem.Current] : null;
            ContentAreaItem item;
            if (wanted[i].InlineType is { } ownType && owned[i] is { } own)
            {
                // A block of the content's own folder, taken over: left as it is when the item is an exact copy of it,
                // else a new version of it with what the item gives.
                item = own.Exact
                    ? NewAreaItem(own.Block.ContentLink.ToReferenceWithoutVersion(), items[i].DisplayOption, takenOver)
                    : SharedItem(ownType, items[i].Properties, items[i].Name, items[i].DisplayOption, takenOver, own.Block, $"{path}[{i}]");
            }
            else if (wanted[i].InlineType is { } type)
            {
                if (takenOver is null)
                {
                    // A new block; a changed item that takes over one is that block, changed.
                    Claim(type, $"{path}[{i}]");
                }
                item = takenOver is null && Shared is not null
                    ? SharedItem(type, items[i].Properties, items[i].Name, items[i].DisplayOption, null, null, $"{path}[{i}]")
                    : InlineItem(type, items[i].Properties, items[i].Name, items[i].DisplayOption, takenOver, keepValues: pairs[i].Exact, $"{path}[{i}]");
            }
            else
            {
                item = NewAreaItem(wanted[i].Link!, items[i].DisplayOption, takenOver);
            }
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
    /// A current item that a new block item of a whole area takes over as a shared block of the content's own "For this
    /// page" folder (<see cref="TakeOverOwnBlocks"/>): its position, whether the item is an exact copy of the block, the
    /// block's latest version, and its values as a block (<see cref="Plain"/>).
    /// </summary>
    private sealed record OwnTakeover(int Current, bool Exact, IContent Block, BlockData Values);

    /// <summary>
    /// Where new blocks are shared (<see cref="Shared"/>): what the new block items (<c>{type, properties}</c>) of a whole
    /// area that <paramref name="pairs"/> (the inline rules) left unpaired take over, so writing an area whole again (as a
    /// plan run again does) changes the blocks it has rather than making more, and an inline block it has stays inline. In
    /// this order, each pairing unique:
    /// <list type="number">
    /// <item>the inline rules (<see cref="AreaItemRules.Pair"/>, done already): an exact copy of an inline block, then the
    /// only inline block of its type left for the only item of the type left;</item>
    /// <item>an exact copy of a shared block of its type from the content's own "For this page" folder;</item>
    /// <item>by name: an inline block of its type with the item's name, or a folder block of its type with the item's name
    /// and display option, when that is the only one either side has;</item>
    /// <item>the only one of its type left, inline or in the folder, for the only item of the type left.</item>
    /// </list>
    /// An item that pairs with nothing becomes a new shared block. A shared block from anywhere else is never taken over:
    /// it stays as its <c>{ref}</c>. Inline pairings are added to <paramref name="pairs"/>; folder ones are returned.
    /// </summary>
    /// <param name="types">The type of each new block item; null for an item that shows content.</param>
    private OwnTakeover?[] TakeOverOwnBlocks(IReadOnlyList<ContentAreaItem> current, AreaItemRules.Pairing[] pairs, IReadOnlyList<ContentType?> types,
        IReadOnlyList<AreaItemValue> items)
    {
        var owned = new OwnTakeover?[items.Count];
        if (Shared is null)
        {
            return owned;
        }
        var waiting = Enumerable.Range(0, items.Count).Where(i => pairs[i].Current < 0 && types[i] is not null).ToList();
        if (waiting.Count == 0)
        {
            return owned;
        }
        var taken = pairs.Where(p => p.Current >= 0).Select(p => p.Current).ToHashSet();
        var repository = call.Service<IContentTypeRepository>();
        var candidates = Enumerable.Range(0, current.Count).Where(j => !taken.Contains(j))
            .Select(j => (Index: j, Block: Shared.OwnBlock(current[j])))
            .Where(c => c.Block is not null && repository.Load(c.Block.ContentTypeID) is not null)
            .Select(c => (c.Index, Block: c.Block!, Values: Plain(c.Block!, repository.Load(c.Block!.ContentTypeID)!)))
            .ToList();
        bool FolderAlike(int i, int j) =>
            string.Equals((items[i].Name ?? "").Trim(), candidates[j].Block.Name, StringComparison.Ordinal)
            && string.Equals(items[i].DisplayOption ?? "", PropertyValues.DisplayOption(current[candidates[j].Index]) ?? "", StringComparison.OrdinalIgnoreCase);
        var folderTaken = new HashSet<int>();
        void TakeFolder(int i, int j, bool exact)
        {
            var (index, block, values) = candidates[j];
            owned[i] = new OwnTakeover(index, exact, block, values);
            folderTaken.Add(j);
        }

        // 2. Exact copies of the folder's blocks (those alike first, so identical blocks pair with the one the item names).
        if (candidates.Count > 0)
        {
            var exact = AreaItemRules.Pair(
                candidates.Select(c => (string?)AreaItemRules.InlineKey(c.Block.ContentTypeID)).ToList(),
                waiting.Select(i => (string?)AreaItemRules.InlineKey(types[i]!.ID)).ToList(),
                (w, j) => Unchanged(candidates[j].Values, items[waiting[w]]),
                (w, j) => FolderAlike(waiting[w], j));
            for (var w = 0; w < waiting.Count; w++)
            {
                if (exact[w] is { Current: >= 0, Exact: true } pairing)
                {
                    TakeFolder(waiting[w], pairing.Current, exact: true);
                }
            }
        }

        // What is left on either side: the items, the inline blocks no item took over, the folder's blocks.
        List<int> Unpaired() => waiting.Where(i => pairs[i].Current < 0 && owned[i] is null).ToList();
        var inlineLeft = Enumerable.Range(0, current.Count).Where(j => !taken.Contains(j) && InlineBlocks.Of(current[j]) is not null).ToList();
        int InlineType(int j) => BlockTypeId(InlineBlocks.Of(current[j])!);
        // A candidate on the current side: an inline block by its position (j >= 0), or a folder block (-1 - its candidate index).
        IEnumerable<int> Left(int typeId) =>
            inlineLeft.Where(j => !pairs.Any(p => p.Current == j) && InlineType(j) == typeId)
                .Concat(Enumerable.Range(0, candidates.Count).Where(j => !folderTaken.Contains(j) && candidates[j].Block.ContentTypeID == typeId).Select(j => -1 - j));
        void Take(int i, int candidate)
        {
            if (candidate >= 0)
            {
                pairs[i] = new AreaItemRules.Pairing(candidate, false);
            }
            else
            {
                TakeFolder(i, -1 - candidate, exact: false);
            }
        }

        // 3. By name, where that is unambiguous on both sides.
        bool Named(int i, int candidate) => !string.IsNullOrWhiteSpace(items[i].Name) && (candidate >= 0
            ? string.Equals(items[i].Name!.Trim(), InlineBlocks.Name(current[candidate]) ?? "", StringComparison.Ordinal)
            : FolderAlike(i, -1 - candidate));
        var named = Unpaired().Select(i => (Item: i, Matches: Left(types[i]!.ID).Where(c => Named(i, c)).ToList())).ToList();
        foreach (var (i, matches) in named)
        {
            if (matches is [var match] && named.Count(n => n.Matches.Contains(match)) == 1)
            {
                Take(i, match);
            }
        }

        // 4. The only one of its type left, for the only item of the type left.
        foreach (var group in Unpaired().GroupBy(i => types[i]!.ID).Where(g => g.Count() == 1))
        {
            if (Left(group.Key).ToList() is [var only])
            {
                Take(group.Single(), only);
            }
        }
        return owned;
    }

    /// <summary>
    /// <paramref name="block"/>'s values (a shared block's version) on a block of <paramref name="type"/> as the writer
    /// builds one, to compare and pair with what an item gives as it does an inline block's.
    /// </summary>
    private BlockData Plain(IContent block, ContentType type)
    {
        var plain = blocks.Create(type);
        foreach (var property in plain.Property.Where(p => !p.IsMetaData))
        {
            if (block.Property[property.Name] is not { } stored)
            {
                continue;
            }
            if (stored.IsNull)
            {
                property.Clear();
            }
            else
            {
                property.Value = stored.Value;
            }
        }
        return plain;
    }

    /// <summary>
    /// For an editor: a shared block of the content's own folder that a changed item takes over gets only what the item
    /// gives, as a changed inline block does; it mustn't lose values they can't see or change (<see cref="LostUnseen"/>).
    /// </summary>
    private void RequireNoOwnBlockLostUnseen(IReadOnlyList<OwnTakeover?> owned, IReadOnlyList<AreaItemValue> items, string path)
    {
        if (!call.Properties.Checks)
        {
            return;
        }
        for (var i = 0; i < owned.Count; i++)
        {
            if (owned[i] is not { Exact: false } own)
            {
                continue;
            }
            var given = items[i].Properties?.Keys.ToList() ?? [];
            if (LostUnseen(own.Values, Fresh(own.Values), $"{path}[{own.Current}]", path, own.Current, given).FirstOrDefault() is { Value: not null } lost)
            {
                var link = own.Block.ContentLink.ToReferenceWithoutVersion();
                throw new AgentException(AgentErrorCodes.Usage,
                    $"{path} would lose {lost.Value} of the shared block {link} ('{own.Block.Name}'), which you can't see or change in the CMS edit UI: only an exact copy of the block (as get_content shows it) keeps it.",
                    $"Give that item back as {{\"ref\": \"{link}\"}} and change the block itself (content {link}), or give its values as get_content shows them. Nothing was saved.")
                {
                    Reason = AgentErrorReasons.UnseenValues,
                };
            }
        }
    }

    /// <summary>
    /// An item for a shared block in the content's "For this page" folder (<see cref="Shared"/>): a new one (it carries the
    /// block's GUID until the block is saved), or <paramref name="existing"/> taken over, whose areas pair with what the
    /// item gives for them. Its values are built as a new inline block's are, and saved with the content. The item has the
    /// render settings of <paramref name="takenOver"/>; the name, given or not, goes on the block.
    /// </summary>
    private ContentAreaItem SharedItem(ContentType type, IReadOnlyDictionary<string, JsonElement>? values, string? name, string? displayOption,
        ContentAreaItem? takenOver, IContent? existing, string where)
    {
        var block = blocks.Create(type);
        if (existing is not null)
        {
            Baseline(block, Plain(existing, type));
        }
        ApplyTo(block, values, where);
        var guid = existing is null ? Guid.NewGuid() : Guid.Empty;
        // Link only for an existing block; a new one is named by its GUID (a link would be refused on CMS 13 next to it).
        var item = existing is null ? new ContentAreaItem { ContentGuid = guid } : new ContentAreaItem { ContentLink = existing.ContentLink.ToReferenceWithoutVersion() };
        Settings(item, displayOption, takenOver);
        if (_trial == 0)
        {
            Shared!.Add(new SharedAreaBlocks.Pending { Path = where, Type = type, Values = block, GivenName = name, Guid = guid, Existing = existing });
        }
        return item;
    }

    /// <summary>Whether an item gives a current inline block's name and display option (as stored, or by the same name).</summary>
    private static bool Alike(AreaItemValue item, ContentAreaItem current) =>
        string.Equals((item.Name ?? "").Trim(), InlineBlocks.Name(current) ?? "", StringComparison.Ordinal)
        && string.Equals(item.DisplayOption ?? "", PropertyValues.DisplayOption(current) ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For an editor: no inline block the area has now may go (be left out, or replaced by an item that isn't an exact copy
    /// of it) while it, or a local block or inline block inside it at any depth, has values they can't see or change that the
    /// write wouldn't keep. A changed item that takes over a block keeps what a ContentArea it gives keeps (that write checks
    /// itself). The edit UI lets them remove such a block on purpose (areaOps remove, also by a nested area's path); a whole
    /// area written back mustn't drop them unseen.
    /// </summary>
    private void RequireNothingLostUnseen(IReadOnlyList<ContentAreaItem> current, IReadOnlyList<AreaItemRules.Pairing> pairs, IReadOnlyList<AreaItemValue> items, string path)
    {
        if (!call.Properties.Checks)
        {
            return;
        }
        for (var j = 0; j < current.Count; j++)
        {
            if (InlineBlocks.Of(current[j]) is not { } block || pairs.Any(p => p.Exact && p.Current == j))
            {
                continue;
            }
            var by = Enumerable.Range(0, pairs.Count).Where(i => pairs[i].Current == j).Select(i => items[i]).FirstOrDefault();
            var given = by?.Properties?.Keys.ToList() ?? [];
            if (LostUnseen(block, Fresh(block), $"{path}[{j}]", path, j, given).FirstOrDefault() is { Value: not null } lost)
            {
                throw new AgentException(AgentErrorCodes.Usage,
                    $"{path} would lose {lost.Value}, which you can't see or change in the CMS edit UI: only an exact copy of the block it is in (as get_content shows it) keeps it.",
                    $"Give that item back as get_content shows it, change one inline block's values by its position (\"{path}[{j}]\": {{...}}, or areaOps set), or, if the user wants that block gone, remove it on purpose with areaOps remove (property \"{lost.Area}\", index {lost.Index}). Nothing was saved.")
                {
                    Reason = AgentErrorReasons.UnseenValues,
                };
            }
        }
    }

    /// <summary>A new block of <paramref name="block"/>'s type, as the CMS makes one: what a value left out becomes.</summary>
    private BlockData? Fresh(BlockData block) =>
        call.Service<IContentTypeRepository>().Load(BlockTypeId(block)) is { } type ? blocks.Create(type) : null;

    /// <summary>
    /// The values of <paramref name="block"/> (at <paramref name="path"/>) the caller can't see or change that would be
    /// lost, when it becomes <paramref name="fresh"/> (null: nothing of it stays): its own, and those of its local blocks and
    /// of the inline blocks in its ContentAreas, but those of the areas in <paramref name="given"/> (written whole, which
    /// checks itself). Each with the area and position of the inline block it is in.
    /// </summary>
    private IEnumerable<(string? Value, string Area, int Index)> LostUnseen(IContentData block, IContentData? fresh, string path, string area, int index, IReadOnlyList<string> given)
    {
        var values = PropertyValues.Snapshot(block);
        var defaults = fresh is null ? new Dictionary<string, JsonElement?>() : PropertyValues.Snapshot(fresh);
        foreach (var property in block.Property.Where(p => !p.IsMetaData).ToList())
        {
            var at = $"{path}.{property.Name}";
            if (call.Properties.Access(block, property) != PropertyAccess.Editable)
            {
                if (values.GetValueOrDefault(property.Name)?.GetRawText() != defaults.GetValueOrDefault(property.Name)?.GetRawText())
                {
                    yield return (at, area, index);
                }
                continue;
            }
            if (property.Value is ContentArea inner && !given.Any(g => g.Equals(property.Name, StringComparison.OrdinalIgnoreCase)
                || (AreaItemRules.Indexed(g) is { } item && item.Property.Equals(property.Name, StringComparison.OrdinalIgnoreCase))))
            {
                for (var k = 0; k < inner.Items.Count; k++)
                {
                    if (InlineBlocks.Of(inner.Items[k]) is { } nested)
                    {
                        foreach (var lost in LostUnseen(nested, Fresh(nested), $"{at}[{k}]", at, k, []))
                        {
                            yield return lost;
                        }
                    }
                }
            }
            else if (property.Value is BlockData local and not IContent)
            {
                foreach (var lost in LostUnseen(local, fresh?.Property[property.Name]?.Value as IContentData, at, area, index, []))
                {
                    yield return lost;
                }
            }
            else if (property.Value is System.Collections.IEnumerable list and not string and not ContentArea
                && property.PropertyValueType is { IsGenericType: true } listType && listType.GetGenericArguments() is [var element]
                && typeof(BlockData).IsAssignableFrom(element))
            {
                // A block list's items: a rebuilt block's list starts from the new block's, given or not, so nothing of
                // the replaced block's items carries over (BlockList keeps unseen values only from the list it replaces).
                var k = 0;
                foreach (var listItem in list.OfType<IContentData>().ToList())
                {
                    foreach (var lost in LostUnseen(listItem, blocks.Create(listItem.GetOriginalType()), $"{at}[{k}]", area, index, []))
                    {
                        yield return lost;
                    }
                    k++;
                }
            }
        }
    }

    /// <summary>
    /// A warning when inline blocks of a type lose their render settings or personalization because which changed item
    /// replaces which can't be told (several of the type changed, or several left): the positions, so the caller can change
    /// those blocks by position instead.
    /// </summary>
    private void WarnSettingsDropped(IReadOnlyList<ContentAreaItem> current, IReadOnlyList<AreaItemRules.Pairing> pairs, IReadOnlyList<string?> wanted, string path)
    {
        if (_trial > 0)
        {
            return;
        }
        var taken = pairs.Where(p => p.Current >= 0).Select(p => p.Current).ToHashSet();
        var dropped = Enumerable.Range(0, current.Count)
            .Where(j => !taken.Contains(j) && InlineBlocks.Of(current[j]) is not null && Key(current[j]) is { } key
                && Enumerable.Range(0, wanted.Count).Any(i => pairs[i].Current < 0 && string.Equals(wanted[i], key, StringComparison.OrdinalIgnoreCase))
                && (!string.IsNullOrWhiteSpace(current[j].ContentGroup) || current[j].AllowedRoles?.Any() == true
                    || CmsApi.RenderSettings(current[j]).Any(s => s.Key != PropertyValues.DisplayOptionKey && s.Key != InlineBlocks.NameKey)))
            .ToList();
        if (dropped.Count > 0)
        {
            var kept = dropped.SelectMany(j => CmsApi.RenderSettings(current[j]).Select(s => s.Key).Where(k => k != PropertyValues.DisplayOptionKey && k != InlineBlocks.NameKey)
                .Concat(!string.IsNullOrWhiteSpace(current[j].ContentGroup) || current[j].AllowedRoles?.Any() == true ? ["personalization"] : [])).Distinct();
            Warnings.Add(new ValidationIssue(path,
                $"The inline block(s) at position(s) {string.Join(", ", dropped)} of the area as it was lose their {string.Join(", ", kept)}: the changed items of their type can't be told apart, so none takes them over. To keep them, change those blocks by position ({path}[n]) instead of writing the area whole.",
                "warning"));
        }
    }

    /// <summary>
    /// Whether <paramref name="item"/> gives <paramref name="block"/> (an inline block, or a shared block's values) as it is: every value it has (that
    /// the caller sees), and those values set on a copy of it change nothing. Its name, display option and personalization
    /// don't count; they are as given either way.
    /// </summary>
    private bool Unchanged(BlockData? block, AreaItemValue item)
    {
        if (block is null)
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
        _trial++;
        try
        {
            Apply(copy, given);
        }
        catch (AgentException)
        {
            // Values it can't take: the write reports them for the item it pairs with.
            return false;
        }
        finally
        {
            _trial--;
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
        if (Shared is null)
        {
            // A shared block needs no inline blocks in the CMS.
            InlineBlocks.Require();
        }
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

    /// <summary>
    /// A new item with a new inline block of <paramref name="type"/> (name, id or GUID), with <paramref name="values"/> set;
    /// null when <paramref name="unlessIn"/> has one like it already (<see cref="HasInlineLike"/>), which is checked before
    /// anything a new block needs (the right to create it).
    /// </summary>
    public ContentAreaItem? NewInlineItem(string type, IReadOnlyDictionary<string, JsonElement>? values, string? name, string? displayOption, string where,
        IEnumerable<ContentAreaItem>? unlessIn = null)
    {
        var contentType = InlineType(type, where);
        if (Shared is not null)
        {
            // A shared block in the content's folder, as the site's edit UI makes it.
            var mark = Shared.Count;
            var shared = SharedItem(contentType, values, name, displayOption, null, null, where);
            if (unlessIn is not null && HasSharedLike(unlessIn, Shared.Of(shared)!, values, name) is { } like)
            {
                Shared.Truncate(mark);
                if (Shared.OwnBlock(like) is { } found)
                {
                    // Reported, so a plan run again reaches the block as it did when the add made it.
                    Shared.Find(found);
                }
                return null;
            }
            Claim(contentType, where);
            return shared;
        }
        var item = InlineItem(contentType, values, name, displayOption, null, keepValues: false, where);
        if (unlessIn is not null && HasInlineLike(unlessIn, item, values, name))
        {
            return null;
        }
        Claim(contentType, where);
        return item;
    }

    /// <summary>
    /// Whether <paramref name="items"/> has an inline block like the one <paramref name="added"/> shows: of its type with
    /// that name, when one is given (its values may have changed since, as a plan's later steps change them); without a
    /// name, of its type with the values of <paramref name="given"/> (as the new block has them).
    /// </summary>
    public bool HasInlineLike(IEnumerable<ContentAreaItem> items, ContentAreaItem added, IReadOnlyDictionary<string, JsonElement>? given, string? name)
    {
        var block = InlineBlocks.Of(added)!;
        var typeId = BlockTypeId(block);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return items.Any(item => InlineBlocks.Of(item) is { } named && BlockTypeId(named) == typeId
                && string.Equals(InlineBlocks.Name(item) ?? "", name.Trim(), StringComparison.Ordinal));
        }
        var wanted = PropertyValues.Snapshot(block);
        return items.Any(item => InlineBlocks.Of(item) is { } existing && BlockTypeId(existing) == typeId && SameValues(existing, wanted, given));
    }

    /// <summary>Whether <paramref name="existing"/> has the values <paramref name="given"/> names as <paramref name="wanted"/> has them (or as the CMS stores them).</summary>
    private bool SameValues(IContentData existing, IReadOnlyDictionary<string, JsonElement?> wanted, IReadOnlyDictionary<string, JsonElement>? given) =>
        PropertyValues.Snapshot(existing) is var has
        && (given ?? new Dictionary<string, JsonElement>()).All(pair =>
            has.GetValueOrDefault(pair.Key)?.GetRawText() == wanted.GetValueOrDefault(pair.Key)?.GetRawText()
            || (pair.Value.ValueKind == JsonValueKind.String && StoredAlready(Value(existing, pair.Key), pair.Value.GetString()!)));

    /// <summary>
    /// Where new blocks are shared: the item of <paramref name="items"/> that shows a block like <paramref name="added"/>, of
    /// its type with that name when one is given, else with the values of <paramref name="given"/>: a shared block of the
    /// content's own folder (by its name), an inline block it had already (by its name in the area), or a block this write
    /// makes. Null when there is none.
    /// </summary>
    private ContentAreaItem? HasSharedLike(IEnumerable<ContentAreaItem> items, SharedAreaBlocks.Pending added, IReadOnlyDictionary<string, JsonElement>? given, string? name)
    {
        var typeId = added.Type.ID;
        var wanted = PropertyValues.Snapshot(added.Values);
        foreach (var item in items)
        {
            var (candidate, candidateName) = Shared!.Of(item) is { } pending
                ? (pending.Type.ID == typeId ? pending.Values : null, pending.GivenName)
                : InlineBlocks.Of(item) is { } inline
                    ? (BlockTypeId(inline) == typeId ? inline : null, InlineBlocks.Name(item))
                    : Shared.OwnBlock(item, typeId) is { } own ? ((IContentData?)own, own.Name) : (null, null);
            if (candidate is null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(name)
                ? string.Equals(candidateName?.Trim() ?? "", name.Trim(), StringComparison.Ordinal)
                : SameValues(candidate, wanted, given))
            {
                return item;
            }
        }
        return null;
    }

    private static object? Value(IContentData content, string name) =>
        content.Property.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>
    /// Whether <paramref name="given"/>, text for a property that holds <paramref name="stored"/>, is what the CMS stores
    /// already: rich text that differs only in how its tags are written (<see cref="RichTextMarkup"/>), or a link to the
    /// same place, which the CMS stores as a permanent link (<c>/en/about/</c> as <c>~/link/{guid}.aspx</c>).
    /// </summary>
    private bool StoredAlready(object? stored, string given) => stored switch
    {
        ContentArea => false,
        XhtmlString html => RichTextMarkup.Same(html.ToInternalString(), given),
        Url url => string.Equals(Permanent(url.OriginalString), Permanent(given), StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>A link as the CMS stores it: a permanent link for content of the site, without its leading "~".</summary>
    private string Permanent(string url)
    {
        var mapped = !string.IsNullOrWhiteSpace(url) && call.OptionalService<IUrlResolver>() is { } urls
            && urls.TryToPermanent(url, out var permanent) && !string.IsNullOrEmpty(permanent) ? permanent : url;
        return mapped.TrimStart('~');
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
    /// An item with an inline block of <paramref name="type"/>, with <paramref name="values"/> set on it: a copy of
    /// <paramref name="takenOver"/>'s block when <paramref name="keepValues"/>, else a new one as the CMS makes it (with
    /// the type's default values; see <see cref="Claim"/>). It has the render settings of <paramref name="takenOver"/>.
    /// </summary>
    /// <param name="name">The name in the area, as given (like the display option): null or <c>""</c> for none.</param>
    private ContentAreaItem InlineItem(ContentType type, IReadOnlyDictionary<string, JsonElement>? values, string? name, string? displayOption,
        ContentAreaItem? takenOver, bool keepValues, string where)
    {
        var current = takenOver is null ? null : InlineBlocks.Of(takenOver);
        BlockData block;
        if (keepValues && current is not null)
        {
            block = (BlockData)((EPiServer.Data.Entity.IReadOnly)current).CreateWritableClone();
        }
        else
        {
            block = blocks.Create(type);
            // A changed block: what it gives for its areas pairs with the areas of the block it replaces.
            Baseline(block, current);
        }
        ApplyTo(block, values, where);
        var item = new ContentAreaItem();
        InlineBlocks.Set(item, block, type.ID);
        Settings(item, displayOption, takenOver, inlineName: name ?? "");
        return item;
    }

    /// <summary>The ContentAreas of a rebuilt block (and of its local blocks), with those of the block it replaces (<see cref="_areaBaselines"/>).</summary>
    private void Baseline(IContentData rebuilt, IContentData? replaced)
    {
        if (replaced is null)
        {
            return;
        }
        foreach (var property in rebuilt.Property.Where(p => !p.IsMetaData))
        {
            if (replaced.Property[property.Name] is not { } old)
            {
                continue;
            }
            if (property is PropertyContentArea)
            {
                _areaBaselines[property] = old.Value as ContentArea;
            }
            else if (property.Value is BlockData local and not IContent && old.Value is IContentData oldLocal)
            {
                Baseline(local, oldLocal);
            }
        }
    }

    /// <summary>
    /// What a new block in an area (not a copy of one the area has) takes: for an editor, the right to create a block of its
    /// type, as the edit UI offers types for a new block of the content (its "For this page" folder's).
    /// </summary>
    private void Claim(ContentType type, string where)
    {
        if (_trial > 0)
        {
            return;
        }
        if (call.Service<IContentTypeRepository>().Load(typeof(ContentAssetFolder)) is { } assets && !call.MayCreate(type, assets))
        {
            throw AgentException.Usage($"{where}: you may not create {type.Name} blocks: the content type's access rights, or those of its group of types, don't allow it.");
        }
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
