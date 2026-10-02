using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Safety;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Http;

/// <summary>Checks that run before any route: Development only, loopback only, per-run token.</summary>
internal static class RequestGuard
{
    /// <summary>Headers that proxies, load balancers and tunnels (ngrok, Cloudflare, dev tunnels) add.</summary>
    internal static readonly string[] ProxyHeaders =
    [
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP", "X-Original-For", "X-Client-IP",
        "True-Client-IP", "CF-Connecting-IP", "CF-Ray", "Via", "X-Ms-Forwarded-Host", "Ngrok-Trace-Id",
    ];

    /// <returns>The first proxy header the request has; null for none.</returns>
    internal static string? ProxyHeader(IHeaderDictionary headers) => ProxyHeaders.FirstOrDefault(headers.ContainsKey);

    /// <exception cref="AgentException">The request is refused.</exception>
    public static void Check(HttpContext context, AgentSettings settings)
    {
        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        if (!environment.IsDevelopment())
        {
            throw AgentException.Refused(
                $"The opticli agent only runs in Development; this site runs as '{environment.EnvironmentName}'.",
                "Start the site with ASPNETCORE_ENVIRONMENT=Development (opticli serve does this).");
        }

        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
        {
            throw AgentException.Refused("The opticli agent only accepts connections from this machine.");
        }

        // Behind a proxy or tunnel on this machine, a remote caller looks local: only the token would still stop it.
        if (ProxyHeader(context.Request.Headers) is { } header)
        {
            throw AgentException.Refused(
                $"The opticli agent refuses requests that came through a proxy or tunnel (they carry {header}).",
                "Call the site directly on 127.0.0.1, as the opticli CLI does.");
        }

        if (settings.Token is null)
        {
            throw new AgentException(
                AgentErrorCodes.Refused,
                $"The opticli agent is disabled: {AgentProtocol.TokenVariable} is not set in the site's environment.",
                "Start the site with opticli serve, which generates a token.")
            {
                ForcedStatus = StatusCodes.Status503ServiceUnavailable,
            };
        }

        if (!TokenComparer.Matches(context.Request.Headers[AgentProtocol.TokenHeader].ToString(), settings.Token))
        {
            throw new AgentException(
                AgentErrorCodes.Unauthorized,
                $"Missing or wrong {AgentProtocol.TokenHeader} header.",
                "Send the token from the opticli serve state file; it changes every time the site is started.");
        }
    }
}
