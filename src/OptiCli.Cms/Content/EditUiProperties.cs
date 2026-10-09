using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>How the CMS edit UI shows a property to the signed-in editor.</summary>
internal enum PropertyAccess
{
    /// <summary>Shown, and the editor may change it.</summary>
    Editable,

    /// <summary>Shown, but locked.</summary>
    ReadOnly,

    /// <summary>Not shown at all.</summary>
    Hidden,
}

/// <summary>
/// The CMS edit UI's own metadata for a content item's properties, as it builds it for the current principal: with the
/// editor descriptors and metadata extenders a site registers, which may hide or lock a property per role. The MCP
/// module provides it from the CMS UI, which the content operations don't reference.
/// </summary>
internal interface IEditUiMetadata
{
    /// <summary>
    /// The edit UI's metadata for <paramref name="owner"/>: content, with the local blocks in it as the edit UI nests them
    /// (each block by instance), or a block on its own (a block list's item, a block just made).
    /// </summary>
    /// <returns>
    /// For the owner and each block in it, every property the edit UI hides or locks, by name, with how; null when it
    /// can't tell. Then the model's own settings decide what is shown, and nothing of it may be changed
    /// (<see cref="EditUiProperties"/>).
    /// </returns>
    IReadOnlyDictionary<IContentData, IReadOnlyDictionary<string, PropertyAccess>>? Restricted(IContentData owner);
}

/// <summary>
/// Which properties a caller may see and change: for the developer every one; for an editor what the CMS edit UI shows
/// them, and of that only what it lets them change. Built-in metadata is a matter for <see cref="PropertyWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// The CMS checks none of this on save: a property hidden from the edit UI (an import id, a field a site fills in code,
/// an administrators' "header scripts" field) could otherwise be read and written through opticli by any editor.
/// </para>
/// <para>
/// From the model and the site's settings, a property is hidden when it isn't displayed in edit mode (admin mode's
/// setting, <c>[ScaffoldColumn(false)]</c>), or is on a tab whose required access the editor lacks on the content (also
/// in an inline block or a block list's item, by the content it is in; not in a local block, as the edit UI does); and
/// read-only with <c>[Editable(false)]</c> or <c>[ReadOnly(true)]</c>. The CMS UI's own metadata
/// (<see cref="IEditUiMetadata"/>, where the caller has it) adds what editor descriptors and metadata extenders hide or
/// lock. The edit UI also locks a property that isn't culture-specific outside the master language; opticli refuses
/// that change with a message of its own, and shows the value, so that lock isn't taken from the metadata.
/// </para>
/// <para>
/// When the CMS UI's metadata can't be built for an owner, the model's settings decide what is shown, and nothing of the
/// owner may be changed: what an editor descriptor locks would otherwise be open.
/// </para>
/// </remarks>
internal sealed class EditUiProperties(CmsCall call, bool check)
{
    /// <summary>The edit UI's metadata by owner; null for an owner it couldn't be built for.</summary>
    private readonly Dictionary<IContentData, IReadOnlyDictionary<string, PropertyAccess>?> _metadata = new(ReferenceEqualityComparer.Instance);

    private IReadOnlyList<TabDefinition>? _tabs;

    /// <summary>The blocks that aren't content this call has seen a property hold: in which content, and whether as a local block.</summary>
    private readonly Dictionary<IContentData, (IContent? Content, bool Local)> _held = new(ReferenceEqualityComparer.Instance);

    /// <summary>The content last asked about: what a block no property was seen to hold (one a write just made) is in.</summary>
    private IContent? _content;

    /// <summary>Whether anything is checked: for an editor; never for the developer, who sees and changes everything.</summary>
    public bool Checks => check;

    /// <summary>How the edit UI shows <paramref name="property"/> of <paramref name="owner"/> (content, or a block in it) to the caller.</summary>
    public PropertyAccess Access(IContentData owner, PropertyData property)
    {
        if (!check)
        {
            return PropertyAccess.Editable;
        }
        Hold(owner, property);
        var member = property.IsMetaData ? null : owner.GetOriginalType().GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
        var attributes = member?.GetCustomAttributes(inherit: true).OfType<Attribute>().ToList() ?? [];
        // Built-in metadata the writer allows (PageURLSegment, ...) is on the edit UI's settings, whatever its definition says.
        if ((!property.IsMetaData && !property.DisplayEditUI) || attributes.OfType<ScaffoldColumnAttribute>().Any(a => !a.Scaffold) || !TabShown(owner, property))
        {
            return PropertyAccess.Hidden;
        }
        var access = attributes.OfType<EditableAttribute>().Any(a => !a.AllowEdit) || attributes.OfType<ReadOnlyAttribute>().Any(a => a.IsReadOnly)
            ? PropertyAccess.ReadOnly
            : PropertyAccess.Editable;
        if (!property.IsMetaData && Metadata(owner)?.GetValueOrDefault(property.Name) is { } restricted && restricted > access
            && !(restricted == PropertyAccess.ReadOnly && LockedForLanguage(owner, property)))
        {
            access = restricted;
        }
        return access;
    }

    /// <summary>Whether the caller sees <paramref name="property"/> at all: <c>get_content</c> leaves out the rest, as the edit UI does.</summary>
    /// <remarks>A read-only property is left out too: what the editor can't change is no business of their assistant.</remarks>
    public bool Shown(IContentData owner, PropertyData property) => Access(owner, property) == PropertyAccess.Editable;

    /// <summary>
    /// <paramref name="changes"/> without those to properties of <paramref name="content"/> the caller doesn't see, so a
    /// diff (a draft's changes, what a discard loses) never shows a hidden property's value; also inside a local block or
    /// a block list, whose values a diff shows whole.
    /// </summary>
    public IReadOnlyList<PropertyChange> Shown(IContentData content, IReadOnlyList<PropertyChange> changes) =>
        !check ? changes : [.. changes
            .Where(c => content.Property[c.Property] is not { } property || Shown(content, property))
            .Select(c => content.Property[c.Property] is { } property ? c with { Before = Shown(property, c.Before), After = Shown(property, c.After) } : c)];

    /// <summary>
    /// A block's or a block list's value as a diff shows it, without the properties the caller doesn't see: those of the
    /// block itself, or of the list's items as they are now. A list with no items now can't tell, and shows nothing. A
    /// ContentArea's inline blocks likewise, each by an inline block of its type the area has now (or none of their values).
    /// </summary>
    private JsonElement? Shown(PropertyData property, JsonElement? value)
    {
        switch (property.Value)
        {
            case ContentArea area when value is { ValueKind: JsonValueKind.Array } areaItems:
                var inline = area.Items.Select(Compat.InlineBlocks.Of).OfType<BlockData>().ToList();
                if (inline.Count == 0 && !areaItems.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("properties", out _)))
                {
                    return value;
                }
                var types = call.Service<IContentTypeRepository>();
                string? TypeName(BlockData block) => (Compat.InlineBlocks.TypeId(block) is var id and > 0 ? types.Load(id) : types.Load(block.GetOriginalType()))?.Name;
                return JsonSerializer.SerializeToElement(areaItems.EnumerateArray().Select(item =>
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("properties", out var values) || values.ValueKind != JsonValueKind.Object)
                    {
                        return item;
                    }
                    var type = item.TryGetProperty("type", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
                    var like = inline.FirstOrDefault(b => string.Equals(TypeName(b), type, StringComparison.OrdinalIgnoreCase));
                    var shown = item.EnumerateObject().Where(p => p.Name != "properties").ToDictionary(p => p.Name, p => (JsonElement?)p.Value, StringComparer.Ordinal);
                    if (like is not null)
                    {
                        shown["properties"] = Shown(like, values);
                    }
                    return JsonSerializer.SerializeToElement(shown);
                }).ToList());
            case IContentData block when value is { ValueKind: JsonValueKind.Object } json:
                return Shown(block, json);
            case System.Collections.IEnumerable items and not string when property.PropertyValueType.IsGenericType
                && property.PropertyValueType.GetGenericArguments() is [var element] && typeof(BlockData).IsAssignableFrom(element):
                var blocks = items.OfType<IContentData>().ToList();
                if (value is not { ValueKind: JsonValueKind.Array } array)
                {
                    return value;
                }
                if (blocks.Count == 0)
                {
                    return null;
                }
                return JsonSerializer.SerializeToElement(array.EnumerateArray()
                    .Select((item, i) => item.ValueKind == JsonValueKind.Object ? Shown(blocks[Math.Min(i, blocks.Count - 1)], item) : item)
                    .ToList());
            default:
                return value;
        }
    }

    private JsonElement Shown(IContentData block, JsonElement json)
    {
        var shown = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var field in json.EnumerateObject())
        {
            if (block.Property[field.Name] is not { } property)
            {
                shown[field.Name] = field.Value;
            }
            else if (Shown(block, property))
            {
                shown[field.Name] = Shown(property, field.Value);
            }
        }
        return JsonSerializer.SerializeToElement(shown);
    }

    /// <summary>
    /// A property type's settings, without content: hidden when it isn't displayed in edit mode or has
    /// <c>[ScaffoldColumn(false)]</c>, read-only with <c>[Editable(false)]</c> or <c>[ReadOnly(true)]</c>. What depends
    /// on the content (a tab's required access, editor descriptors) is only known for an item.
    /// </summary>
    public bool Shown(PropertyDefinition definition, PropertyInfo? member)
    {
        if (!check)
        {
            return true;
        }
        var attributes = member?.GetCustomAttributes(inherit: true).OfType<Attribute>().ToList() ?? [];
        return definition.DisplayEditUI
            && !attributes.OfType<ScaffoldColumnAttribute>().Any(a => !a.Scaffold)
            && !attributes.OfType<EditableAttribute>().Any(a => !a.AllowEdit)
            && !attributes.OfType<ReadOnlyAttribute>().Any(a => a.IsReadOnly);
    }

    /// <exception cref="AgentException">
    /// <c>usage</c> for a property the edit UI hides from the caller or doesn't let them change; <c>refused</c> when the
    /// CMS UI's metadata couldn't be built for the owner, so whether it does can't be told.
    /// </exception>
    public void RequireEditable(IContentData owner, PropertyData property)
    {
        if (Access(owner, property) != PropertyAccess.Editable)
        {
            throw AgentException.Usage(
                $"'{property.Name}' is not editable in the CMS edit UI for you, so it can't be changed here either.",
                $"Leave it as it is, or ask someone who may change it to do so in the CMS. Properties you may set: {string.Join(", ", owner.Property.Where(p => !p.IsMetaData && Shown(owner, p)).Select(p => p.Name))}.");
        }
        if (check && !property.IsMetaData && MetadataFailed(owner))
        {
            throw AgentException.Refused(
                $"Whether the CMS edit UI lets you change '{property.Name}' couldn't be told: the site's editing settings for {owner.GetOriginalType().Name} couldn't be read (the site's log says why).",
                "Ask the user to make this change in the CMS edit UI. Nothing was changed.");
        }
    }

    /// <summary>
    /// A tab's required access (admin mode's tabs, <c>[RequiredAccess]</c> on a <c>[GroupDefinitions]</c> constant): the
    /// edit UI hides the tab's properties from an editor without that access to the content. For a block that isn't
    /// content it does as its forms do:
    /// <list type="bullet">
    /// <item>
    /// An inline block in a ContentArea, or a block list's item, at any depth: by the editor's access to the content being
    /// edited. Its form is built for a new block of its type below that content, which gets the content's access rights.
    /// </item>
    /// <item>A local block: shown, whatever the tab. Its properties are nested in its owner's form, which no access rights go with.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Two quirks of the edit UI are left out, as they would let an assistant change what the content's access rights
    /// keep from the editor: the new block is made by the editor, so the content's "Creator" entries apply to them; and a
    /// block list inside a local or an inline block has its items' form built below the start page. A block a write just
    /// made (no property was seen to hold it yet) is an inline block or a list item, so the content asked about decides;
    /// with none, a tab that requires access is hidden.
    /// </remarks>
    private bool TabShown(IContentData owner, PropertyData property)
    {
        _tabs ??= [.. call.Service<ITabDefinitionRepository>().List()];
        var tab = _tabs.FirstOrDefault(t => t.ID == property.OwnerTab);
        if (tab is null || tab.RequiredAccess == AccessLevel.NoAccess)
        {
            return true;
        }
        if (owner is IContent)
        {
            return owner is not ISecurable content || HasAccess(content, tab.RequiredAccess);
        }
        var (within, local) = _held.TryGetValue(owner, out var held) ? held : (_content, false);
        return local || (within is ISecurable securable && HasAccess(securable, tab.RequiredAccess));
    }

    private bool HasAccess(ISecurable content, AccessLevel level) =>
        content.GetSecurityDescriptor().HasAccess(call.Service<IPrincipalAccessor>().Principal, level);

    /// <summary>
    /// Remembers which content the blocks that <paramref name="property"/> of <paramref name="owner"/> holds are in, and
    /// how: a local block, the inline blocks of a ContentArea, a block list's items. Content asked about becomes the
    /// content of a block no property was seen to hold.
    /// </summary>
    private void Hold(IContentData owner, PropertyData property)
    {
        if (owner is IContent content)
        {
            _content = content;
        }
        // By the value's type first: no other property's value is loaded for this.
        var type = property.PropertyValueType;
        var blockList = type.IsGenericType && type.GetGenericArguments() is [var element] && typeof(BlockData).IsAssignableFrom(element);
        if (!typeof(BlockData).IsAssignableFrom(type) && !typeof(ContentArea).IsAssignableFrom(type) && !blockList)
        {
            return;
        }
        var within = owner as IContent ?? (_held.TryGetValue(owner, out var held) ? held.Content : _content);
        switch (property.Value)
        {
            case BlockData local and not IContent:
                _held.TryAdd(local, (within, true));
                break;
            case ContentArea area:
                foreach (var inline in area.Items.Select(Compat.InlineBlocks.Of).OfType<BlockData>())
                {
                    _held.TryAdd(inline, (within, false));
                }
                break;
            case System.Collections.IEnumerable items when blockList:
                foreach (var item in items.OfType<BlockData>().Where(b => b is not IContent))
                {
                    _held.TryAdd(item, (within, false));
                }
                break;
        }
    }

    /// <summary>The edit UI's metadata for <paramref name="owner"/>; empty without the CMS UI, null when it couldn't be built.</summary>
    private IReadOnlyDictionary<string, PropertyAccess>? Metadata(IContentData owner)
    {
        if (_metadata.TryGetValue(owner, out var known))
        {
            return known;
        }
        if (call.OptionalService<IEditUiMetadata>() is not { } metadata)
        {
            return _metadata[owner] = new Dictionary<string, PropertyAccess>();
        }
        var built = metadata.Restricted(owner);
        // The local blocks of content come with it, as the edit UI nests them.
        foreach (var (inner, restricted) in built ?? new Dictionary<IContentData, IReadOnlyDictionary<string, PropertyAccess>>())
        {
            _metadata.TryAdd(inner, restricted);
        }
        if (built is null || !built.ContainsKey(owner))
        {
            _metadata[owner] = null;
        }
        return _metadata[owner];
    }

    private bool MetadataFailed(IContentData owner)
    {
        Metadata(owner);
        return _metadata[owner] is null;
    }

    /// <summary>The edit UI's lock on a property that isn't culture-specific, in a language branch other than the master.</summary>
    private static bool LockedForLanguage(IContentData owner, PropertyData property) =>
        !property.IsLanguageSpecific && owner is ILocalizable { Language: { } language, MasterLanguage: { } master } && !language.Equals(master);
}
