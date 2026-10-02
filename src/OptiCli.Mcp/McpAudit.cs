using Microsoft.Extensions.Logging;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp;

/// <summary>
/// The audit trail, as ordinary log entries in the <c>OptiCli.Mcp.Audit</c> category, so a site routes them wherever
/// its other logs go: every authorization decision, token issue, refresh, revocation and tool call, with the user, the
/// client and the outcome. Never tokens, codes, secrets or property values.
/// </summary>
internal sealed class McpAudit(ILoggerFactory loggers)
{
    public const string Category = "OptiCli.Mcp.Audit";

    private readonly ILogger _logger = loggers.CreateLogger(Category);

    /// <summary>The consent page's outcome: allowed, denied by the editor, or refused by the role gate.</summary>
    public void Authorize(string user, string clientId, string clientName, string outcome, string scope) =>
        _logger.LogInformation(
            "MCP authorize {Outcome}: user {User}, client {ClientName} ({ClientId}), scope {Scope}",
            outcome, Clean(user), Clean(clientName), Clean(clientId), scope);

    /// <summary>A code exchanged for tokens: a new connection.</summary>
    public void TokenIssued(Grant grant) =>
        _logger.LogInformation(
            "MCP token issued: user {User}, client {ClientName} ({ClientId}), scope {Scope}, grant {GrantId}",
            Clean(grant.UserName), Clean(grant.ClientName), Clean(grant.ClientId), grant.Scope, grant.GrantId);

    /// <summary>A refresh: tokens rotated, or the grant ended because the editor no longer passes the role gate.</summary>
    public void Refresh(Grant grant, string outcome) =>
        _logger.LogInformation(
            "MCP refresh {Outcome}: user {User}, client {ClientName} ({ClientId}), grant {GrantId}",
            outcome, Clean(grant.UserName), Clean(grant.ClientName), Clean(grant.ClientId), grant.GrantId);

    /// <summary>A token request that failed: the reason only, never what was sent.</summary>
    public void TokenRefused(string grantType, string clientId, string error) =>
        _logger.LogInformation("MCP token refused: grant type {GrantType}, client {ClientId}, {Error}", Clean(grantType), Clean(clientId), error);

    /// <summary>A connection revoked on the connections page.</summary>
    public void Revoked(string by, Grant grant) =>
        _logger.LogInformation(
            "MCP connection revoked by {By}: user {User}, client {ClientName} ({ClientId}), grant {GrantId}",
            Clean(by), Clean(grant.UserName), Clean(grant.ClientName), Clean(grant.ClientId), grant.GrantId);

    /// <summary>A tool call and how it ended; <paramref name="contentRefs"/> are the content references it named.</summary>
    public void ToolCall(string user, string clientId, string tool, IReadOnlyList<string> contentRefs, string outcome) =>
        _logger.LogInformation(
            "MCP tool {Tool} {Outcome}: user {User}, client {ClientId}, content {ContentRefs}",
            Clean(tool), outcome, Clean(user), Clean(clientId), contentRefs.Count == 0 ? "-" : Clean(string.Join(", ", contentRefs)));

    /// <summary>
    /// Values a caller chose (a client id, a client's name) without control characters and cut to a sane length, so they
    /// can't forge log lines or flood the log.
    /// </summary>
    internal static string Clean(string value)
    {
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > 200 ? clean[..200] + "…" : clean;
    }
}
