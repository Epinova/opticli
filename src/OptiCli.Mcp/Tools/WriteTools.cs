using System.ComponentModel;
using System.Text.Json;
using EPiServer.Cms.Shell.UI.Configurations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using OptiCli.Cms.Operations;
using OptiCli.Mcp.OAuth;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// Changes, saved as drafts by default, as the editor and with opticli's own checks: validation, approval sequences,
/// <c>baseVersion</c> conflicts and someone else's pending changes. Publishing through these tools needs what
/// <see cref="PublishTools"/> needs.
/// </summary>
[McpServerToolType]
internal sealed class WriteTools(IHttpContextAccessor http, IOptions<OptiCliMcpOptions> options, ILoggerFactory loggers)
    : ContentToolBase(http, options, loggers)
{
    internal const string DryRun = "Only validate and report the changes, saving nothing: do this first and show the user the changes.";

    internal const string Publish = "Publish now instead of saving a draft: only when the user asked for it, after confirming with them.";

    internal const string RequestApproval = "Send it for review where an approval sequence applies (the CMS's Ready for Review); refused where none does.";

    internal const string PublishAt = "Schedule publishing for this time (ISO 8601 with offset, e.g. 2026-10-05T08:00:00+02:00): only when the user asked for it.";

    internal const string IncludeDraft = "Also put live the unpublished changes someone else saved (the pendingDraft of a refused call): only after asking the user.";

    internal const string Properties = "Property values by name, e.g. {\"Heading\": \"Text\", \"MainBody\": \"<p>XHTML</p>\"}: strings, numbers, booleans, content refs, ContentArea arrays of {ref} (a shared block) or {type, properties} (an inline block, values as plain values: a whole area replaces the area, and an inline item keeps a block's other values only as an exact copy of it); \"MainArea[2]\": {...} sets values of the one inline block at that position; null clears. Names and types: get_content_type.";

    [McpServerTool(Name = "create_content", Title = "Create content", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a page, block or folder as a draft below parent (or in the \"For this page\" folder of forContent). Check allowed child types with get_content_type first. Returns editUrl: give it to the user to review the draft in the CMS.")]
    public string CreateContent(
        [Description("The content type, e.g. ArticlePage.")] string type,
        [Description("The new content's name.")] string name,
        [Description("The parent page or folder: id or GUID. Give this or forContent.")] string? parent = null,
        [Description("Put it in the \"For this page\" assets folder of this content (id or GUID), as blocks used only there are.")] string? forContent = null,
        [Description("Language, e.g. en; the parent's master language when left out.")] string? lang = null,
        [Description(Properties)] Dictionary<string, JsonElement>? properties = null,
        [Description(Publish)] bool publish = false,
        [Description(RequestApproval)] bool requestApproval = false,
        [Description(PublishAt)] DateTime? publishAt = null,
        [Description(DryRun)] bool dryRun = false) =>
        Write(Scopes.Write, call => CreateOperation.Run(call, new CreateRequest
        {
            Type = type,
            Name = name,
            Parent = parent,
            ForContent = forContent,
            Lang = lang,
            Properties = properties,
            Publish = publish,
            RequestApproval = requestApproval,
            PublishAt = publishAt,
            DryRun = dryRun,
        }), PublishingIf(publish || publishAt is not null));

    [McpServerTool(Name = "update_content", Title = "Update content", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Changes existing content: saves a new draft from the latest version with the given properties, name or ContentArea edits. Pass baseVersion (the version you read) so someone else's newer save isn't overwritten. Run with dryRun first. Returns editUrl for the user to review the draft.")]
    public string UpdateContent(
        [Description("Content id or GUID.")] string reference,
        [Description(Properties)] Dictionary<string, JsonElement>? properties = null,
        [Description("ContentArea edits applied after properties: {op: add|remove|move|set, property, ref, index, at, displayOption, ifMissing}; add a new inline block with type and values ({Prop: value}) instead of ref; set changes the values (and name) of the inline block at index, the rest of it staying. An inline block has no ref: remove, move and set it by index. property may be an area inside an inline block, by its path: \"MainContentArea[0].Area\".")] List<AreaOperation>? areaOps = null,
        [Description("New name.")] string? name = null,
        [Description("Language branch, e.g. en; the master language when left out.")] string? lang = null,
        [Description("The version you read (get_content's version); refused if a newer one was saved since.")] int? baseVersion = null,
        [Description(DryRun)] bool dryRun = false,
        [Description(Publish)] bool publish = false,
        [Description(RequestApproval)] bool requestApproval = false,
        [Description(PublishAt)] DateTime? publishAt = null,
        [Description(IncludeDraft)] bool includeDraft = false) =>
        Write(Scopes.Write, call => DraftOperation.Run(call, reference, new DraftRequest
        {
            Properties = properties,
            AreaOps = areaOps,
            Name = name,
            Lang = lang,
            BaseVersion = baseVersion,
            DryRun = dryRun,
            Publish = publish,
            RequestApproval = requestApproval,
            PublishAt = publishAt,
            IncludeDraft = includeDraft,
        }), PublishingIf(publish || publishAt is not null));

    [McpServerTool(Name = "add_language", Title = "Add language", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Translates content: creates a new language branch as a draft, starting from the master language's values, with the given name and properties. Returns editUrl.")]
    public string AddLanguage(
        [Description("Content id or GUID.")] string reference,
        [Description("The new branch's language, e.g. sv; must be enabled on the site.")] string lang,
        [Description("The name in that language; the master language's when left out.")] string? name = null,
        [Description(Properties)] Dictionary<string, JsonElement>? properties = null,
        [Description(Publish)] bool publish = false,
        [Description(RequestApproval)] bool requestApproval = false,
        [Description(DryRun)] bool dryRun = false) =>
        Write(Scopes.Write, call => LanguagesOperation.Run(call, reference, new LanguageBranchRequest
        {
            Lang = lang,
            Name = name,
            Properties = properties,
            Publish = publish,
            RequestApproval = requestApproval,
            DryRun = dryRun,
        }), PublishingIf(publish));

    [McpServerTool(Name = "discard_draft", Title = "Discard draft", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes one draft version for good (the latest by default); never the published version or one published before. Confirm with the user first; dryRun shows what would be lost.")]
    public string DiscardDraft(
        [Description("Content id or GUID, or the version itself (123_456).")] string reference,
        [Description("The version id to discard; the latest when left out.")] int? version = null,
        [Description("Language branch whose latest version to discard; the master language when left out.")] string? lang = null,
        [Description("Discard it although someone else saved it: only after asking the user.")] bool includeDraft = false,
        [Description("Only report what would be discarded.")] bool dryRun = false) =>
        Write(Scopes.Write, call => DiscardOperation.Run(call, reference, new DiscardRequest
        {
            Version = version,
            Lang = lang,
            IncludeDraft = includeDraft,
            DryRun = dryRun,
        }));

    [McpServerTool(Name = "upload_media", Title = "Upload media", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Uploads a file (base64) as new media below parent (a media folder) or in forContent's \"For this page\" folder, with the media type the site maps its extension to; or a new file for existing media (replace). Returns editUrl.")]
    public string UploadMedia(
        [Description("File name with extension, e.g. team.jpg; also the name unless name is given.")] string fileName,
        [Description("The file's content, base64. Leave out only with dryRun.")] string? data = null,
        [Description("The media folder: id or GUID.")] string? parent = null,
        [Description("Put it in the \"For this page\" folder of this content (id or GUID).")] string? forContent = null,
        [Description("The media item's name.")] string? name = null,
        [Description("A media type that accepts the extension, when the site has several.")] string? type = null,
        [Description(Properties)] Dictionary<string, JsonElement>? properties = null,
        [Description("Existing media (id or GUID) to give this file instead, as a new version.")] string? replace = null,
        [Description(Publish)] bool publish = false,
        [Description(RequestApproval)] bool requestApproval = false,
        [Description(DryRun)] bool dryRun = false) =>
        Write(Scopes.Write, call => UploadOperation.Run(call, new UploadRequest
        {
            FileName = fileName,
            Data = data,
            Parent = parent,
            ForContent = forContent,
            Name = name,
            Type = type,
            Properties = properties,
            Replace = replace,
            Publish = publish,
            RequestApproval = requestApproval,
            DryRun = dryRun,
        }), editor =>
        {
            ToolGates.Upload(fileName, data, Site, CmsUploadRules.From(RequestService<UploadOptions>()));
            PublishingIf(publish)?.Invoke(editor);
        });

    [McpServerTool(Name = "move_content", Title = "Move content", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Moves content (with everything below it) under another parent, which changes its URL. Confirm with the user first; dryRun checks it.")]
    public string MoveContent(
        [Description("Content id or GUID.")] string reference,
        [Description("The new parent: id or GUID.")] string destination,
        [Description("Only check that the move is allowed.")] bool dryRun = false) =>
        Move(Scopes.Write, call => MoveOperation.Run(call, reference, new MoveRequest { Parent = destination, DryRun = dryRun }));
}

/// <summary>Deleting, always to the recycle bin; only registered when the site allows it (<see cref="OptiCliMcpOptions.AllowDelete"/>).</summary>
[McpServerToolType]
internal sealed class DeleteTool(IHttpContextAccessor http, IOptions<OptiCliMcpOptions> options, ILoggerFactory loggers)
    : ContentToolBase(http, options, loggers)
{
    public const string ToolName = "delete_content";

    [McpServerTool(Name = ToolName, Title = "Delete content", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Moves content, with everything below it, to the CMS recycle bin, where an editor can restore it. Confirm with the user first.")]
    public string DeleteContent([Description("Content id or GUID.")] string reference) =>
        Move(Scopes.Write, call => DeleteOperation.Run(call, reference), _ => ToolGates.Deleting(Site));
}
