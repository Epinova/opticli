using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using OptiCli.Cms.Operations;
using OptiCli.Mcp.OAuth;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// Changing what visitors see: only when the site allows it (<see cref="OptiCliMcpOptions.AllowPublish"/>) and the
/// editor granted <see cref="Scopes.Publish"/>, and then as far as their access rights and approval sequences allow.
/// Registered either way, so an assistant asked to publish learns why it can't, and what to do instead.
/// </summary>
[McpServerToolType]
internal sealed class PublishTools(IHttpContextAccessor http, IOptions<OptiCliMcpOptions> options, ILoggerFactory loggers)
    : ContentToolBase(http, options, loggers)
{
    [McpServerTool(Name = "publish_content", Title = "Publish content", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Publishes a saved version (the latest by default), now or at publishAt; or, with requestApproval, sends it for review. Only when the user asked to publish: call it with dryRun first, show the user what goes live (and whose unpublished changes, pendingDraft), and publish once they confirm. Where the site makes new area blocks shared, it also publishes the drafts of the blocks in the content's own \"For this page\" folder that its ContentAreas show and that were never published (sharedBlocks lists them); a block that is live already keeps its newer draft unless named in blocks, and the warnings say which blocks stay behind. Returns url (the public address), editUrl, previewUrl while the version isn't live yet (scheduled, or in review) and siteChanges (what the site's own code did on publish). " + ResultUrls.AssistantPreviewNote)]
    public string PublishContent(
        [Description("Content id or GUID, or a version (123_456).")] string reference,
        [Description("The version id to publish; the latest when left out.")] int? version = null,
        [Description("Language branch whose latest version to publish; the master language when left out.")] string? lang = null,
        [Description(WriteTools.PublishAt)] DateTime? publishAt = null,
        [Description(WriteTools.RequestApproval + " Needs no publishing rights.")] bool requestApproval = false,
        [Description(WriteTools.IncludeDraft)] bool includeDraft = false,
        [Description("Only check the publish and report what would go live, saving nothing: the version, its pendingDraft, validation, the blocks it would publish along (sharedBlocks) and those it would leave behind (warnings). Do this first and show the user.")] bool dryRun = false,
        [Description("Refs of blocks of the content's own \"For this page\" folder that it shows and that are live already, whose newer drafts should go live with it (a dry run's warnings name them): only when the user wants those changes live too. Any other block is refused.")] string[]? blocks = null,
        [Description("Refs of blocks of the content's own \"For this page\" folder that it shows and that were never published, to leave as drafts: the content then goes live without them showing. Only when the user wants them kept back.")] string[]? skipBlocks = null) =>
        // A review request never publishes for an editor (CmsCall.RequestApprovalMayPublish: refused where no sequence
        // applies), so it needs no publish gate; the call's own gate on every publishing save backs that up. A dry run of a
        // publish passes the same gates as the publish, as update_content's dry run with publish does.
        Write(Scopes.Write, call => PublishOperation.Run(call, reference, new PublishRequest
        {
            Version = version,
            Lang = lang,
            PublishAt = publishAt,
            RequestApproval = requestApproval,
            IncludeDraft = includeDraft,
            DryRun = dryRun,
            WithBlocks = blocks is { Length: > 0 } ? blocks : null,
            WithoutBlocks = skipBlocks is { Length: > 0 } ? skipBlocks : null,
        }), PublishingIf(!requestApproval || publishAt is not null));

    [McpServerTool(Name = "unpublish_content", Title = "Unpublish content", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Takes a language branch offline: visitors no longer see it, and its versions are kept. Only when the user asked for it, after confirming.")]
    public string UnpublishContent(
        [Description("Content id or GUID.")] string reference,
        [Description("Language branch, e.g. en; the master language when left out.")] string? lang = null,
        [Description("Only check that it can be taken offline.")] bool dryRun = false) =>
        Write(Scopes.Write, call => UnpublishOperation.Run(call, reference, new UnpublishRequest { Lang = lang, DryRun = dryRun }), PublishingIf(true));
}
