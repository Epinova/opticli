using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using OptiCli.Cms;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// The editor a tool call runs as: the MCP endpoint's principal, which the bearer handler built from the grant. It is
/// also <c>HttpContext.User</c>, and so what the CMS's <c>IPrincipalAccessor</c> gives every content operation, which
/// therefore checks the editor's own access rights.
/// </summary>
internal sealed class McpEditor
{
    private readonly HttpContext _context;
    private readonly IReadOnlyList<string> _scopes;

    private McpEditor(HttpContext context)
    {
        _context = context;
        // The grant's scope string is in the site's order (Scopes.Grantable); keep it.
        _scopes = (context.User.FindFirst(McpClaims.Scope)?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <exception cref="McpException">There is no request, or it isn't one the bearer handler signed in.</exception>
    public static McpEditor From(IHttpContextAccessor accessor) =>
        accessor.HttpContext is { User.Identity: { IsAuthenticated: true, AuthenticationType: TokenService.AuthenticationType } } context
            ? new McpEditor(context)
            : throw new McpException("This tool only runs on the MCP endpoint, for a signed-in editor.");

    public ClaimsPrincipal User => _context.User;

    public string Name => User.Identity?.Name ?? "";

    public string ClientId => User.FindFirst(McpClaims.Client)?.Value ?? "";

    public string ClientName => User.FindFirst(McpClaims.ClientName)?.Value ?? "";

    /// <summary>The roles the grant carries (virtual roles are worked out by the CMS on each check).</summary>
    public IReadOnlyList<string> Roles => User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

    /// <summary>The scopes the editor approved, in the site's order.</summary>
    public IReadOnlyList<string> Scopes => _scopes;

    /// <exception cref="McpException"><c>refused</c> when the connection wasn't granted <paramref name="scope"/> (<see cref="ToolGates.Scope"/>).</exception>
    public void Require(string scope) => ToolGates.Scope(_scopes, scope);

    /// <summary>
    /// A content operation as this editor: every load and save checked against their access rights, every save that
    /// publishes or schedules against the publish gate (<see cref="ToolGates.Publishing"/>) as well, whatever the
    /// tool's arguments said, and every change that deletes someone else's work for good against the delete gate
    /// (<see cref="ToolGates.Deleting"/>).
    /// </summary>
    public CmsCall Call(OptiCliMcpOptions site) =>
        new(_context.RequestServices, _context.RequestAborted, CmsCaller.Editor, () => ToolGates.Publishing(_scopes, site), () => ToolGates.Deleting(site));
}
