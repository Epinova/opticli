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
    [Description("Publishes a saved version (the latest by default), now or at publishAt; or, with requestApproval, sends it for review. Only when the user asked to publish, after confirming what goes live. Returns editUrl.")]
    public string PublishContent(
        [Description("Content id or GUID, or a version (123_456).")] string reference,
        [Description("The version id to publish; the latest when left out.")] int? version = null,
        [Description("Language branch whose latest version to publish; the master language when left out.")] string? lang = null,
        [Description(WriteTools.PublishAt)] DateTime? publishAt = null,
        [Description(WriteTools.RequestApproval + " Needs no publishing rights.")] bool requestApproval = false,
        [Description(WriteTools.IncludeDraft)] bool includeDraft = false) =>
        Write(Scopes.Write, call => PublishOperation.Run(call, reference, new PublishRequest
        {
            Version = version,
            Lang = lang,
            PublishAt = publishAt,
            RequestApproval = requestApproval,
            IncludeDraft = includeDraft,
        }), PublishingIf(!requestApproval || publishAt is not null));

    [McpServerTool(Name = "unpublish_content", Title = "Unpublish content", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Takes a language branch offline: visitors no longer see it, and its versions are kept. Only when the user asked for it, after confirming.")]
    public string UnpublishContent(
        [Description("Content id or GUID.")] string reference,
        [Description("Language branch, e.g. en; the master language when left out.")] string? lang = null,
        [Description("Only check that it can be taken offline.")] bool dryRun = false) =>
        Write(Scopes.Write, call => UnpublishOperation.Run(call, reference, new UnpublishRequest { Lang = lang, DryRun = dryRun }), PublishingIf(true));
}
