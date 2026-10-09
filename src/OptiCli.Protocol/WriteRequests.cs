using System.Text.Json;
using System.Text.Json.Serialization;

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
/// <item>ContentArea: array of <see cref="AreaItemValue"/>, replacing all items. <c>"MainArea[2]"</c>: an object of
/// values for the inline block at that position (zero-based), the others staying as they are.</item>
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

    /// <summary>
    /// CMS 13: the whole Visual Builder composition of an experience (or of a section), replacing its structure: see
    /// <see cref="CompositionNodeValue"/>. Applied after <see cref="AreaOps"/>, before <see cref="CompositionOps"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompositionNodeValue? Composition { get; init; }

    /// <summary>CMS 13: Visual Builder composition edits, applied in order after <see cref="Composition"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CompositionOperation>? CompositionOps { get; init; }

    /// <summary>
    /// CMS 13: change this content variation (its key) instead of the content itself: a new version of the variation,
    /// based on its newest version, or, for a variation that has none in the branch yet, on the branch's published version
    /// (else its latest). <see cref="BaseVersion"/> is checked against the variation's newest version. Pages and experiences
    /// only. Null: the content itself; a ref naming a variation's version changes that variation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Variation { get; init; }

    /// <summary>Publish the new version instead of leaving it as a draft.</summary>
    public bool Publish { get; init; }

    /// <summary>
    /// With <see cref="Publish"/>: also publish the unpublished changes that someone other than opticli saved after the
    /// published version (see <see cref="PendingDraft"/>). Without it such a publish fails with <c>conflict</c> and
    /// <see cref="AgentError.PendingDraft"/>; a dry run reports them in <see cref="WriteResult.PendingDraft"/>.
    /// </summary>
    public bool IncludeDraft { get; init; }

    /// <summary>
    /// Where a content approval sequence applies: save the version and start the sequence (<c>SaveAction.RequestApproval</c>)
    /// instead of publishing, which is refused there (<c>refused</c>, <see cref="AgentErrorReasons.ApprovalSequence"/>).
    /// With <see cref="Publish"/>, content without a sequence is published as usual; without it, such content is a <c>usage</c> error.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequestApproval { get; init; }

    /// <summary>
    /// Schedule the publish for this time (UTC) instead of publishing now (<c>SaveAction.Schedule</c>, with the version's
    /// start-publish date set to it): it stays a draft (<c>delayedPublish</c>) until the CMS's scheduled job publishes it.
    /// Must be in the future. The rules for a publish apply: other people's drafts (<see cref="IncludeDraft"/>), approval
    /// sequences (refused there).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PublishAt { get; init; }

    /// <summary>Apply and validate without saving; the response shows what would change.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The version id the caller last read. If the latest version in <see cref="Lang"/> is a
    /// different one, the request fails with <c>conflict</c> (409) and nothing is saved.
    /// </summary>
    public int? BaseVersion { get; init; }

    /// <summary>
    /// Base the new version on this one instead of the latest: <see cref="FromPublished"/> (the branch's published
    /// version) or a version id of the content in <see cref="Lang"/>. <see cref="BaseVersion"/> is still checked against
    /// the latest version. Not with a ref that names a version (<c>usage</c>). Newer versions the change leaves out are
    /// listed in <see cref="WriteResult.LeftOut"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; init; }

    /// <summary>The <see cref="From"/> value for the published version.</summary>
    public const string FromPublished = "published";
}

/// <summary>One ContentArea edit. Maps 1:1 to <c>opticli area &lt;ref&gt; &lt;Prop&gt; add|remove|move</c>.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>add</c>: insert <see cref="Ref"/> (a shared block), or a new inline block of <see cref="Type"/> with
/// <see cref="Values"/>, at <see cref="At"/> (default: end), optional <see cref="DisplayOption"/>.</item>
/// <item><c>remove</c>: remove the item at <see cref="Index"/>, or the first item referencing <see cref="Ref"/>.</item>
/// <item><c>move</c>: move the item at <see cref="Index"/> (or the first referencing <see cref="Ref"/>) to position <see cref="At"/>.</item>
/// <item><c>set</c>: set <see cref="Values"/> (and <see cref="Name"/>) on the inline block at <see cref="Index"/>; its other
/// values, render settings and personalization stay.</item>
/// </list>
/// Indexes are zero-based and refer to the area as it is after the previous operations. An inline block has no ref, so
/// remove, move and set name it by its index.
/// </remarks>
public sealed record AreaOperation
{
    /// <summary>One of <see cref="AreaOps"/>.</summary>
    public required string Op { get; init; }

    public required string Property { get; init; }

    /// <summary>Content id or GUID of the item to add, remove or move.</summary>
    public string? Ref { get; init; }

    /// <summary>
    /// add only: a new inline block (CMS 12.20+, stored in the area rather than as content of its own) of this block type
    /// (name, id or GUID), instead of <see cref="Ref"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>add with <see cref="Type"/>, set: the inline block's values, with <see cref="DraftRequest.Properties"/>'s value rules.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Values { get; init; }

    /// <summary>
    /// add with <see cref="Type"/>, set: the inline block's name in the area (the edit UI shows it); default for add: none,
    /// which the edit UI shows as the type's name. In a set, <c>""</c> removes it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>Zero-based position of an existing item.</summary>
    public int? Index { get; init; }

    /// <summary>Zero-based target position.</summary>
    public int? At { get; init; }

    public string? DisplayOption { get; init; }

    /// <summary>
    /// add only: do nothing when the area already has an item referencing <see cref="Ref"/> (re-runnable plans); for an
    /// inline block, an inline block of <see cref="Type"/> that has the given <see cref="Values"/> (and <see cref="Name"/>).
    /// </summary>
    public bool IfMissing { get; init; }
}

public static class AreaOps
{
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Move = "move";
    public const string Set = "set";
}

/// <summary>
/// CMS 13: a node of a Visual Builder composition, as a write gives it: the composition itself (the root: an experience's
/// outline, or a section's grid), a section, row, column or component (an element, or a section-enabled block standing in
/// an outline). The shape of <c>ContentItemCompositionNode</c>, which a read returns.
/// </summary>
/// <remarks>
/// <para>Structure, as the CMS validates it: an experience's outline holds sections and section-enabled blocks; a section
/// holds rows; a row holds columns; a column holds element-enabled blocks. A section or component is an inline block (give
/// <see cref="Type"/>, and <see cref="Properties"/>) or a shared block placed by reference (<see cref="Ref"/>).</para>
/// <para>In <see cref="DraftRequest.Composition"/> the tree is the whole composition: a node whose <see cref="Key"/> the
/// composition has keeps its block (the given properties are set on it, the others keep their values), a node without
/// a key is new; display templates and settings, names and order are as given; nodes left out are removed.</para>
/// </remarks>
public sealed record CompositionNodeValue
{
    /// <summary><c>section</c>, <c>row</c>, <c>column</c> or <c>component</c>; null on the root, and where the parent decides.</summary>
    public string? NodeType { get; init; }

    /// <summary>The node's key, as reads show it; null for a new node (the agent makes one).</summary>
    public string? Key { get; init; }

    public string? Name { get; init; }

    /// <summary>A new inline section's or component's block type (name or GUID).</summary>
    public string? Type { get; init; }

    /// <summary>A shared block placed by reference (content id or GUID) instead of an inline one.</summary>
    public string? Ref { get; init; }

    /// <summary>A section blueprint (its content GUID) a new inline section is copied from: its properties and grid, with new keys.</summary>
    public Guid? Blueprint { get; init; }

    /// <summary>A display template key; in a set, <c>""</c> removes it (and its settings).</summary>
    public string? DisplayTemplate { get; init; }

    /// <summary>Display settings by key; in a set they are merged, and a null value removes one.</summary>
    public IReadOnlyDictionary<string, string?>? DisplaySettings { get; init; }

    /// <summary>An inline block's properties, with <see cref="DraftRequest.Properties"/>'s value rules.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    public IReadOnlyList<CompositionNodeValue>? Nodes { get; init; }
}

/// <summary>CMS 13: one edit of a Visual Builder composition. Maps 1:1 to <c>opticli composition &lt;ref&gt; add|remove|move|set</c>.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>add</c>: insert <see cref="Value"/> (with its children) under <see cref="Parent"/> at <see cref="At"/> (default: the end).</item>
/// <item><c>remove</c>: remove <see cref="Node"/> with everything in it.</item>
/// <item><c>move</c>: move <see cref="Node"/> under <see cref="Parent"/> (default: where it is) to <see cref="At"/> (default: the end).</item>
/// <item><c>set</c>: change <see cref="Node"/>'s name, display template and settings, and an inline block's properties, from <see cref="Value"/>.</item>
/// </list>
/// Nodes are named by key, or by name when only one node has it; <c>root</c> (or no <see cref="Parent"/>) is the composition
/// itself. Positions are zero-based among the parent's children, after the previous edits.
/// </remarks>
public sealed record CompositionOperation
{
    /// <summary>One of <see cref="CompositionOps"/>.</summary>
    public required string Op { get; init; }

    public string? Node { get; init; }

    public string? Parent { get; init; }

    public int? At { get; init; }

    public CompositionNodeValue? Value { get; init; }
}

public static class CompositionOps
{
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Move = "move";
    public const string Set = "set";

    /// <summary>What names the composition itself where a node is expected.</summary>
    public const string Root = "root";
}

/// <summary>
/// A ContentArea item as a property value: a shared block (content of its own) by <see cref="Ref"/> or <see cref="Guid"/>,
/// or an inline block (CMS 12.20+, stored in the area rather than as content of its own) by <see cref="Type"/> and
/// <see cref="Properties"/>. Not both.
/// </summary>
/// <remarks>
/// <para>Writing a whole area, an item without <see cref="Group"/> or <see cref="VisitorGroups"/> keeps those of the
/// item it takes over: the n-th item for some content takes over the n-th current item for that content. It also keeps
/// that item's other render settings. <see cref="DisplayOption"/> and an inline block's <see cref="Name"/> are always as
/// given.</para>
/// <para>An inline block is built from what the item gives: a whole area replaces the area. Only an item that is an exact
/// copy of a current inline block (every value it has, as reads show them) keeps that block, with its values the caller
/// can't see. Any other inline item is a new block with just the given values (the type's defaults for the rest); it takes
/// over a current block's render settings and personalization only when that is unambiguous: the only item of its type
/// that isn't a copy, and the only current block of the type not copied. For an editor, an inline block with values they
/// can't see or change can't go that way. One block's values change with <c>"MainArea[2]"</c> or an area <c>set</c>.</para>
/// </remarks>
public sealed record AreaItemValue
{
    public string? Ref { get; init; }

    public Guid? Guid { get; init; }

    /// <summary>True for an inline block, as reads show it; it needs <see cref="Type"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Inline { get; init; }

    /// <summary>An inline block's block type (name, id or GUID).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>An inline block's name in the area (the edit UI shows it), always as given, like <see cref="DisplayOption"/>; null for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>An inline block's values, with <see cref="DraftRequest.Properties"/>'s value rules.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    /// <summary>A display option id the site registers (<c>EPiServer.Web.DisplayOptions</c>); null for none.</summary>
    public string? DisplayOption { get; init; }

    /// <summary>Personalization group: items sharing one are alternatives for different visitor groups. <c>""</c> for none.</summary>
    public string? Group { get; init; }

    /// <summary>Visitor group ids (or roles) the item is shown to. <c>[]</c> for everyone.</summary>
    public IReadOnlyList<string>? VisitorGroups { get; init; }
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

    /// <summary>CMS 13: the new experience's (or section's) Visual Builder composition, as <see cref="DraftRequest.Composition"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompositionNodeValue? Composition { get; init; }

    /// <summary>
    /// CMS 13: make the content from this Visual Builder blueprint (its content GUID), as the edit UI does: a copy of it
    /// with its type, values and composition (and its other languages), named <see cref="Name"/>, below <see cref="Parent"/>.
    /// <see cref="Type"/> must be the blueprint's type. <see cref="Properties"/> and <see cref="Composition"/> are applied
    /// to the copy.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Blueprint { get; init; }

    public bool Publish { get; init; }

    /// <summary>As <see cref="DraftRequest.RequestApproval"/>; for new content, the parent's sequence applies.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequestApproval { get; init; }

    /// <summary>As <see cref="DraftRequest.PublishAt"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PublishAt { get; init; }

    public bool DryRun { get; init; }

    /// <summary>
    /// The new content's GUID, so it is the same in every database it is created in. If content with this GUID
    /// exists, the request fails with <c>conflict</c> unless <see cref="UpdateExisting"/>.
    /// </summary>
    public Guid? Guid { get; init; }

    /// <summary>
    /// Dry run only: check the "allowed below" rule against this content type instead of <see cref="Parent"/>'s, for
    /// content whose real parent doesn't exist yet (a plan that creates both).
    /// </summary>
    public string? ParentType { get; init; }

    /// <summary>
    /// When content with <see cref="Guid"/> exists: update it instead (name and properties, as a new version like
    /// <see cref="AgentRoutes.Draft"/>). It must be of the same type and under the same parent; content in the recycle
    /// bin is moved back under the parent first. The response has <see cref="WriteResult.Existing"/>.
    /// </summary>
    public bool UpdateExisting { get; init; }

    /// <summary>
    /// When <see cref="UpdateExisting"/> publishes existing content: also publish the unpublished changes that someone other than opticli saved after the
    /// published version (see <see cref="PendingDraft"/>). Without it such a publish fails with <c>conflict</c> and
    /// <see cref="AgentError.PendingDraft"/>; a dry run reports them in <see cref="WriteResult.PendingDraft"/>.
    /// </summary>
    public bool IncludeDraft { get; init; }
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

    /// <summary>As <see cref="CreateRequest.RequestApproval"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequestApproval { get; init; }

    /// <summary>Validate type, parent, name and properties without storing a file.</summary>
    public bool DryRun { get; init; }

    /// <summary>As <see cref="CreateRequest.Guid"/>.</summary>
    public Guid? Guid { get; init; }

    /// <summary>
    /// Replace the file of this existing media item instead (content id or GUID): a new version with the new file, of the
    /// same media type, which must accept the file's extension. Give no <see cref="Parent"/>, <see cref="ForContent"/>,
    /// <see cref="Type"/> or <see cref="Guid"/>. With <see cref="Publish"/>, the rules for publishing existing content apply.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Replace { get; init; }

    /// <summary>As <see cref="CreateRequest.UpdateExisting"/>; existing media keeps its file.</summary>
    public bool UpdateExisting { get; init; }

    /// <summary>As <see cref="CreateRequest.IncludeDraft"/>.</summary>
    public bool IncludeDraft { get; init; }
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

    /// <summary>As <see cref="DraftRequest.RequestApproval"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequestApproval { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.RemoveLanguage"/>.</summary>
public sealed record RemoveLanguageRequest
{
    /// <summary>The branch to delete, with all its versions. Not the master language.</summary>
    public required string Lang { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Publish"/> (may be empty).</summary>
public sealed record PublishRequest
{
    /// <summary>
    /// Version id to publish; default is the latest version in <see cref="Lang"/>. Naming the version confirms what goes
    /// live, so it needs no <see cref="IncludeDraft"/>.
    /// </summary>
    public int? Version { get; init; }

    public string? Lang { get; init; }

    /// <summary>
    /// Without <see cref="Version"/>: also publish the unpublished changes that someone other than opticli saved after the
    /// published version (see <see cref="PendingDraft"/>). Without it such a publish fails with <c>conflict</c> and
    /// <see cref="AgentError.PendingDraft"/>; a dry run reports them in <see cref="WriteResult.PendingDraft"/>.
    /// </summary>
    public bool IncludeDraft { get; init; }

    /// <summary>
    /// Start the content's approval sequence for the version instead of publishing it, where one applies; a publish is
    /// refused there. Content without a sequence is published as usual.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequestApproval { get; init; }

    /// <summary>As <see cref="DraftRequest.PublishAt"/>: schedule the version's publish instead.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PublishAt { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Unpublish"/> (may be empty).</summary>
public sealed record UnpublishRequest
{
    /// <summary>The branch to take offline; default is the content's master language.</summary>
    public string? Lang { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Discard"/> (may be empty).</summary>
public sealed record DiscardRequest
{
    /// <summary>The unpublished version to delete; default: the ref's version, else the newest version in <see cref="Lang"/>.</summary>
    public int? Version { get; init; }

    public string? Lang { get; init; }

    /// <summary>
    /// Confirms discarding a version someone other than opticli saved. Without it that fails with <c>conflict</c> and
    /// <see cref="AgentError.PendingDraft"/> (the version and its changes); a dry run reports it in
    /// <see cref="WriteResult.PendingDraft"/>.
    /// </summary>
    public bool IncludeDraft { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Move"/>.</summary>
public sealed record MoveRequest
{
    /// <summary>New parent content id or GUID.</summary>
    public required string Parent { get; init; }

    /// <summary>Run every check (protected content, the type allowed below the new parent) without moving.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.Restore"/>.</summary>
public sealed record RestoreRequest
{
    /// <summary>
    /// The parent to restore below, content id or GUID; default: the parent it had before it was deleted, as the CMS
    /// stored it (what the edit UI's Restore uses).
    /// </summary>
    public string? Parent { get; init; }

    /// <summary>Run every check (where it goes, that the parent can take it) without moving it.</summary>
    public bool DryRun { get; init; }
}
