using System.Text.Json;

namespace OptiCli.Protocol;

/// <summary>
/// Body of <see cref="AgentRoutes.Draft"/>: change properties on a new version of existing content.
/// </summary>
/// <remarks>
/// <para><see cref="Properties"/> maps property name to value. A JSON string is parsed the way the CMS
/// parses imported values (<c>PropertyData.ParseToSelf</c>), so <c>"123"</c> works for a content
/// reference, <c>"2025-01-31"</c> for a date, HTML for XHTML strings. Numbers and booleans are
/// parsed from their JSON text. <c>null</c> clears the property. Structured values are JSON:</para>
/// <list type="bullet">
/// <item>ContentArea: array of <see cref="AreaItemValue"/>, replacing all items.</item>
/// <item>LinkItemCollection: array of <see cref="LinkItemValue"/>.</item>
/// <item>LinkItem: one <see cref="LinkItemValue"/>, or a string href/ref that keeps the link's text.</item>
/// <item>Lists (<c>IList&lt;string&gt;</c>, <c>IList&lt;ContentReference&gt;</c>, ...): array of scalars.</item>
/// <item>Local block properties: object of the block's own property names to values (recursive).</item>
/// <item>Anything else: deserialized into the property's value type.</item>
/// </list>
/// <para>Property names are the CMS names (as in <c>types</c>), case-insensitive.</para>
/// </remarks>
public sealed record DraftRequest
{
    /// <summary>Language branch to change; default is the content's master language.</summary>
    public string? Lang { get; init; }

    /// <summary>New content name, if it should change.</summary>
    public string? Name { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    /// <summary>ContentArea edits, applied in order after <see cref="Properties"/>.</summary>
    public IReadOnlyList<AreaOperation>? AreaOps { get; init; }

    /// <summary>Publish the new version instead of leaving it as a draft.</summary>
    public bool Publish { get; init; }

    /// <summary>Apply and validate without saving; the response shows what would change.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The version id the caller last read. If the latest version in <see cref="Lang"/> is a
    /// different one, the request fails with <c>conflict</c> (409) and nothing is saved.
    /// </summary>
    public int? BaseVersion { get; init; }
}

/// <summary>One ContentArea edit. Maps 1:1 to <c>opticli area &lt;ref&gt; &lt;Prop&gt; add|remove|move</c>.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>add</c>: insert <see cref="Ref"/> at <see cref="At"/> (default: end), optional <see cref="DisplayOption"/>.</item>
/// <item><c>remove</c>: remove the item at <see cref="Index"/>, or the first item referencing <see cref="Ref"/>.</item>
/// <item><c>move</c>: move the item at <see cref="Index"/> (or the first referencing <see cref="Ref"/>) to position <see cref="At"/>.</item>
/// </list>
/// Indexes are zero-based and refer to the area as it is after the previous operations.
/// </remarks>
public sealed record AreaOperation
{
    /// <summary>One of <see cref="AreaOps"/>.</summary>
    public required string Op { get; init; }

    public required string Property { get; init; }

    /// <summary>Content id or GUID of the item to add, remove or move.</summary>
    public string? Ref { get; init; }

    /// <summary>Zero-based position of an existing item.</summary>
    public int? Index { get; init; }

    /// <summary>Zero-based target position.</summary>
    public int? At { get; init; }

    public string? DisplayOption { get; init; }

    /// <summary>add only: do nothing when the area already has an item referencing <see cref="Ref"/> (re-runnable plans).</summary>
    public bool IfMissing { get; init; }
}

public static class AreaOps
{
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Move = "move";
}

/// <summary>A ContentArea item as a property value. Give <see cref="Ref"/> or <see cref="Guid"/>.</summary>
/// <remarks>
/// In responses, inline blocks (CMS 12.20+, stored inside the area rather than as shared content)
/// have neither; they can be moved or removed by index but not created through opticli.
/// </remarks>
public sealed record AreaItemValue
{
    public string? Ref { get; init; }

    public Guid? Guid { get; init; }

    public string? DisplayOption { get; init; }
}

/// <summary>A LinkItemCollection entry. <see cref="Href"/> may be a URL or a content ref (<c>123</c>).</summary>
public sealed record LinkItemValue
{
    public required string Href { get; init; }

    public string? Text { get; init; }

    public string? Title { get; init; }

    public string? Target { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Create"/>: create new content.</summary>
/// <remarks>
/// Give <see cref="Parent"/>, or <see cref="ForContent"/> to create a block in that content's
/// "For this page" assets folder (created if missing). Page types must be allowed below the parent's type.
/// </remarks>
public sealed record CreateRequest
{
    /// <summary>Parent content id or GUID.</summary>
    public string? Parent { get; init; }

    /// <summary>Content id or GUID whose assets folder becomes the parent.</summary>
    public string? ForContent { get; init; }

    /// <summary>Content type name (case-insensitive) or GUID.</summary>
    public required string Type { get; init; }

    public required string Name { get; init; }

    /// <summary>Language of the new content; default is the master language of the parent (or of <see cref="ForContent"/>).</summary>
    public string? Lang { get; init; }

    /// <summary>Same value rules as <see cref="DraftRequest.Properties"/>.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    public bool Publish { get; init; }

    public bool DryRun { get; init; }

    /// <summary>
    /// The new content's GUID, so it is the same in every database it is created in. If content with this GUID
    /// exists, the request fails with <c>conflict</c> unless <see cref="UpdateExisting"/>.
    /// </summary>
    public Guid? Guid { get; init; }

    /// <summary>
    /// When content with <see cref="Guid"/> exists: update it instead (name and properties, as a new version like
    /// <see cref="AgentRoutes.Draft"/>). It must be of the same type and under the same parent; content in the recycle
    /// bin is moved back under the parent first. The response has <see cref="WriteResult.Existing"/>.
    /// </summary>
    public bool UpdateExisting { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Media"/>: upload a file as a new media item (image, PDF, video, ...).</summary>
/// <remarks>
/// Give <see cref="Parent"/> (a folder) or <see cref="ForContent"/>, as for <see cref="CreateRequest"/>. The media type
/// is the one the CMS maps the file's extension to, or <see cref="Type"/>, which must accept the extension.
/// </remarks>
public sealed record UploadRequest
{
    /// <summary>At most this many bytes of file content (before base64).</summary>
    public const int MaxBytes = 50 * 1024 * 1024;

    public string? Parent { get; init; }

    public string? ForContent { get; init; }

    /// <summary>The file's name with its extension, e.g. <c>report.pdf</c>; no directories.</summary>
    public required string FileName { get; init; }

    /// <summary>Content name; default <see cref="FileName"/>.</summary>
    public string? Name { get; init; }

    /// <summary>Media type name or GUID; default: the type the CMS maps the extension to.</summary>
    public string? Type { get; init; }

    /// <summary>Same value rules as <see cref="DraftRequest.Properties"/> (e.g. alt text, copyright).</summary>
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    /// <summary>The file content, base64. Required unless <see cref="DryRun"/>.</summary>
    public string? Data { get; init; }

    public bool Publish { get; init; }

    /// <summary>Validate type, parent, name and properties without storing a file.</summary>
    public bool DryRun { get; init; }

    /// <summary>As <see cref="CreateRequest.Guid"/>.</summary>
    public Guid? Guid { get; init; }

    /// <summary>As <see cref="CreateRequest.UpdateExisting"/>; existing media keeps its file.</summary>
    public bool UpdateExisting { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Languages"/>: create a language branch as a draft.</summary>
public sealed record LanguageBranchRequest
{
    /// <summary>Language code of the new branch, e.g. <c>en</c>. Must be enabled on the site.</summary>
    public required string Lang { get; init; }

    /// <summary>Name in the new language; default is the master language's name.</summary>
    public string? Name { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    public bool Publish { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Publish"/> (may be empty).</summary>
public sealed record PublishRequest
{
    /// <summary>Version id to publish; default is the latest version in <see cref="Lang"/>.</summary>
    public int? Version { get; init; }

    public string? Lang { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Move"/>.</summary>
public sealed record MoveRequest
{
    /// <summary>New parent content id or GUID.</summary>
    public required string Parent { get; init; }
}
