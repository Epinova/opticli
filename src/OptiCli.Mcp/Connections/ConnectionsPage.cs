using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OptiCli.Mcp.OAuth;
using static OptiCli.Mcp.OAuth.HtmlPage;

namespace OptiCli.Mcp.Connections;

/// <summary>
/// <c>{BasePath}/connections</c>: an editor's AI assistant connections (client, scopes, created, last used), each with
/// a Revoke button. CMS administrators (WebAdmins, CmsAdmins) see everyone's and can revoke any. Signed in with the
/// site's own login, like the consent page.
/// </summary>
internal sealed class ConnectionsPage(
    IOAuthStore store,
    GrantCache cache,
    EditorGate gate,
    IAntiforgery antiforgery,
    IOptions<OptiCliMcpOptions> options,
    McpAudit audit)
{
    public async Task<IResult> Show(HttpContext context)
    {
        if (context.User.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } user })
        {
            return Results.Challenge(new AuthenticationProperties { RedirectUri = context.Request.PathBase + context.Request.Path });
        }
        var (current, _) = await gate.CurrentAsync(context.User, context.RequestAborted);
        var admin = gate.IsAdmin(current);
        var grants = await store.ListGrantsAsync(admin ? null : user, context.RequestAborted);
        var form = antiforgery.GetAndStoreTokens(context);
        var action = H(context.Request.PathBase + McpUrls.ConnectionsPath(options.Value));

        string Time(DateTimeOffset? at) => at is { } value ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";
        var rows = string.Concat(grants.Select(g => $"""
            <tr>
              <td>{H(g.ClientName)}<br><code>{H(g.ClientId)}</code></td>
              {(admin ? $"<td>{H(g.UserName)}</td>" : "")}
              <td>{H(g.Scope)}</td>
              <td>{Time(g.Created)}</td>
              <td>{Time(g.LastUsed)}</td>
              <td><form method="post" action="{action}">
                <input type="hidden" name="{H(form.FormFieldName)}" value="{H(form.RequestToken)}">
                <input type="hidden" name="grant" value="{H(g.GrantId)}">
                <button type="submit">Revoke</button>
              </form></td>
            </tr>
            """));
        var table = grants.Count == 0
            ? "<p>No AI assistants are connected.</p>"
            : $"""
              <table>
                <tr><th>App</th>{(admin ? "<th>Editor</th>" : "")}<th>Allowed</th><th>Connected</th><th>Last used</th><th></th></tr>
                {rows}
              </table>
              """;
        var scope = admin ? "every editor's connections (you are a CMS administrator)" : "the AI assistants you connected";
        return Render(context, HttpStatusCode.OK, "AI assistant connections", $"""
            <h1>AI assistant connections</h1>
            <p>Signed in as <b>{H(user)}</b>. These are {scope}. Revoking one cuts the app off at once; it has to be connected again.</p>
            {table}
            """);
    }

    public async Task<IResult> Revoke(HttpContext context)
    {
        if (context.User.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } user })
        {
            return Message(context, HttpStatusCode.Forbidden, "Not signed in", "Sign in to the site first.");
        }
        if (CrossSite(context) is { } crossSite)
        {
            return crossSite;
        }
        if (!context.Request.HasFormContentType || !await antiforgery.IsRequestValidAsync(context))
        {
            return Message(context, HttpStatusCode.BadRequest, "Page expired", "The page expired. Open the connections page again.");
        }
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var grant = form["grant"] is { Count: 1 } id ? await store.FindGrantAsync(id.ToString(), context.RequestAborted) : null;
        var (current, _) = await gate.CurrentAsync(context.User, context.RequestAborted);
        // Someone else's grant looks the same as a missing one, unless you're an administrator.
        if (grant is null || (!string.Equals(grant.UserName, user, StringComparison.OrdinalIgnoreCase) && !gate.IsAdmin(current)))
        {
            return Message(context, HttpStatusCode.NotFound, "Not found", "That connection doesn't exist any more.");
        }
        await store.DeleteGrantAsync(grant.GrantId, context.RequestAborted);
        cache.Evict(grant.GrantId);
        audit.Revoked(user, grant);
        context.Response.Headers.Location = context.Request.PathBase + McpUrls.ConnectionsPath(options.Value);
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }
}
