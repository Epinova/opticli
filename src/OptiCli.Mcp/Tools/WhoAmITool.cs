using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using OptiCli.Mcp.OAuth;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>Who the assistant acts as, and what this connection and site allow.</summary>
[McpServerToolType]
internal sealed class WhoAmITool(IHttpContextAccessor http, IOptions<OptiCliMcpOptions> options)
{
    [McpServerTool(Name = "whoami", Title = "Who am I", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Who the assistant acts as on this site (the signed-in editor), with which roles, which scopes the editor approved, and what the site allows (publishing, deleting, upload size). Every change is made as this editor, with their access rights.")]
    public string WhoAmI()
    {
        var editor = McpEditor.From(http);
        var site = options.Value;
        return JsonSerializer.Serialize(new
        {
            name = editor.Name,
            roles = editor.Roles,
            client = editor.ClientName,
            scopes = editor.Scopes,
            site = new
            {
                allowPublish = site.AllowPublish,
                allowDelete = site.AllowDelete,
                maxUploadBytes = site.MaxUploadBytes,
            },
        }, AgentJson.Options);
    }
}
