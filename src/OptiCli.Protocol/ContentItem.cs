using System.Text.Json;

namespace OptiCli.Protocol;

/// <summary>
/// Response of <c>GET /v1/content/{ref}</c>: one content version exactly as the CMS loads it, in the shape
/// the CLI's DB-direct <c>get</c> uses wherever both can express a value. It exists so the DB read path can
/// be checked against the CMS itself; it never writes.
/// </summary>
public sealed record ContentItem
{
    /// <summary>Content id as a ref, without version (<c>123</c>).</summary>
    public required string Ref { get; init; }

    public required int Id { get; init; }

    /// <summary>The loaded version (<c>tblWorkContent.pkID</c>).</summary>
    public int? Version { get; init; }

    public required Guid Guid { get; init; }

    public string? Name { get; init; }

    public string? Type { get; init; }

    /// <summary><c>page</c>, <c>block</c>, <c>media</c>, <c>folder</c> or <c>other</c>, from the loaded instance's base class.</summary>
    public required string Kind { get; init; }

    /// <summary>The loaded branch; null for language-invariant content (media, folders).</summary>
    public string? Language { get; init; }

    public string? MasterLanguage { get; init; }

    /// <summary>Every existing branch, sorted; null for content that isn't localizable.</summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>CMS version status, camelCase (<c>published</c>, <c>checkedOut</c>, ...).</summary>
    public string? Status { get; init; }

    public string? Parent { get; init; }

    public bool? Deleted { get; init; }

    public DateTime? StartPublish { get; init; }

    public DateTime? StopPublish { get; init; }

    public DateTime? Saved { get; init; }

    public string? ChangedBy { get; init; }

    /// <summary>
    /// What <c>IUrlResolver.GetUrl</c> returns for the item in its language (default context, canonical):
    /// host-relative for the current site, absolute for another site's host; null when not routable.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>Every property that is not CMS metadata, keyed by name.</summary>
    public required IReadOnlyDictionary<string, ContentItemProperty> Properties { get; init; }
}

/// <summary>
/// One property value. <see cref="Value"/> is omitted when the property is empty, and otherwise has these shapes:
/// <list type="bullet">
/// <item>String, LongString, Url, XhtmlString (the stored markup, <c>ToInternalString</c>) and other text: a string.</item>
/// <item>Number, FloatNumber, Boolean: a JSON number or boolean. Date: an ISO 8601 date-time with offset.</item>
/// <item>ContentReference, PageReference: the ref (<c>123</c>, or <c>123__provider</c> for content-provider content).</item>
/// <item>ContentArea: an array of <see cref="ContentItemAreaEntry"/>.</item>
/// <item>LinkItemCollection: an array of <c>{href, text, title, target}</c>; LinkItem: one such object.</item>
/// <item>A local block (<see cref="Type"/> <c>Block</c>): an object of nested properties, keyed by name, in this same shape;
/// a list of blocks (<c>BlockList</c>): an array of such objects.</item>
/// <item>Other lists (ContentReferenceList, StringList, ...): an array of the items in these shapes.</item>
/// <item>Custom property types (lists of the site's own classes, ...): what the property stores (<c>SaveData</c>), parsed
/// when it is JSON, since only the property knows how its items serialise.</item>
/// </list>
/// </summary>
/// <param name="Type">The property definition type name (<c>String</c>, <c>ContentArea</c>, ...), or <c>Block</c>/<c>BlockList</c> for block-typed properties.</param>
public sealed record ContentItemProperty(string Type, JsonElement? Value = null)
{
    /// <summary>For <c>Block</c> and <c>BlockList</c>: the block's content type name.</summary>
    public string? BlockType { get; init; }
}

/// <summary>One ContentArea item.</summary>
/// <param name="Ref">The referenced content; null for inline blocks and for references the CMS could not resolve.</param>
/// <param name="Guid">Set when <see cref="Ref"/> is null but the item names a content GUID.</param>
public sealed record ContentItemAreaEntry(string? Ref, Guid? Guid = null)
{
    /// <summary>True for an inline block (CMS 12.20+), which has no content of its own.</summary>
    public bool? Inline { get; init; }

    /// <summary>For inline blocks: the block's content type name.</summary>
    public string? Type { get; init; }

    /// <summary>For inline blocks: the block's properties, in the <see cref="ContentItemProperty"/> shape.</summary>
    public IReadOnlyDictionary<string, ContentItemProperty>? Properties { get; init; }

    public string? DisplayOption { get; init; }

    /// <summary>Personalization group, when the item is one of several alternatives.</summary>
    public string? Group { get; init; }

    /// <summary>Visitor groups (or roles) the item is shown to; null means everyone.</summary>
    public IReadOnlyList<string>? VisitorGroups { get; init; }
}
