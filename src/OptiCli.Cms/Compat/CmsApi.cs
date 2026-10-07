using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using EPiServer.Validation;

namespace OptiCli.Cms.Compat;

/// <summary>
/// The CMS APIs whose shape differs between CMS 12 and CMS 13, for the operations both agent builds (and the MCP module,
/// CMS 12 only) compile. <c>CMS13</c> is defined for the agent's net10.0 build; the operations themselves have no
/// <c>#if</c>.
/// </summary>
internal static class CmsApi
{
    /// <summary>
    /// Moves content to the recycle bin. CMS 12 records <paramref name="deletedBy"/> and checks Delete for the current
    /// principal; CMS 13 records the current principal and checks <paramref name="access"/>, so the developer passes
    /// <see cref="AccessLevel.NoAccess"/> as for a save (a fresh CMS 13 database grants administrators nothing on content).
    /// </summary>
    public static void MoveToWastebasket(IContentRepository repository, ContentReference link, string deletedBy, AccessLevel access)
    {
#if CMS13
        _ = deletedBy;
        repository.MoveToWastebasket(link, access);
#else
        _ = access;
        repository.MoveToWastebasket(link, deletedBy);
#endif
    }

    /// <summary>
    /// Deletes one version for good. CMS 12 checks nothing; CMS 13 checks <paramref name="access"/> for the current
    /// principal, so the developer passes <see cref="AccessLevel.NoAccess"/> as for a save.
    /// </summary>
    public static void DeleteVersion(EPiServer.Core.IContentVersionRepository versions, ContentReference link, AccessLevel access)
    {
#if CMS13
        versions.Delete(link, access);
#else
        _ = access;
        versions.Delete(link);
#endif
    }

    /// <summary>A property's value as saved to the database (CMS 13 dropped the owner argument).</summary>
    public static object? SaveData(PropertyData property, PropertyDataCollection owner) =>
#if CMS13
        property.SaveData();
#else
        property.SaveData(owner);
#endif

    /// <summary>The context the CMS validates a save with (CMS 13 also gives the validators the content being saved).</summary>
    public static ContentSaveValidationContext SaveValidationContext(IContent content, SaveAction action) =>
#if CMS13
        new(content, action, newVersionRequired: true);
#else
        new(action, newVersionRequired: true);
#endif

    /// <summary>An item's render settings (<c>data-</c> attributes, the display option among them).</summary>
    public static IEnumerable<KeyValuePair<string, object>> RenderSettings(ContentAreaItem? item) =>
#if CMS13
        // Strings on CMS 13; read from a writable item, the getter makes an empty dictionary, which is harmless.
        item?.RenderSettings.Select(s => KeyValuePair.Create(s.Key, (object)s.Value)) ?? [];
#else
        item?.RenderSettings ?? (IEnumerable<KeyValuePair<string, object>>)[];
#endif

    /// <summary>Sets one render setting on a writable item.</summary>
    public static void SetRenderSetting(ContentAreaItem item, string key, object value)
    {
#if CMS13
        // An init-only IDictionary<string, string> on CMS 13, which a writable item creates on first read.
        item.RenderSettings[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
#else
        // Null until something is set on a new item.
        item.RenderSettings ??= new Dictionary<string, object>();
        item.RenderSettings[key] = value;
#endif
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a block type. CMS 12 loads block types as <see cref="BlockType"/>; CMS 13 loads
    /// every non-page type as a plain <see cref="ContentType"/> and says it with <see cref="ContentType.Base"/> (or the
    /// model: a Visual Builder section type has the base <c>Section</c>, and is a block too).
    /// </summary>
    public static bool IsBlockType(ContentType type) =>
#if CMS13
        type.Base == ContentTypeBase.Block || (type.ModelType is { } model && typeof(BlockData).IsAssignableFrom(model));
#else
        type is BlockType;
#endif

    /// <summary>Whether the property is indexed for search (CMS 13 replaced the flag with <c>IndexingType</c>).</summary>
    public static bool IsSearchable(PropertyDefinition definition) =>
#if CMS13
        definition.IndexingType == IndexingType.Searchable;
#else
        definition.Searchable;
#endif

    /// <summary>The value a page's <c>PageShortcutLink</c> takes for <paramref name="target"/>: a page reference on CMS 12, a content reference on CMS 13.</summary>
    public static ContentReference ShortcutLink(PageData target) =>
#if CMS13
        target.ContentLink.ToReferenceWithoutVersion();
#else
        target.PageLink.ToPageReference().ToReferenceWithoutVersion();
#endif
}
