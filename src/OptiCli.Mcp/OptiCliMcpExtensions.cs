using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OptiCli.Mcp.Connections;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp;

/// <summary>Adds the opticli MCP module to a CMS 12 site: <c>services.AddOptiCliMcp()</c>, then <c>endpoints.MapOptiCliMcp()</c>.</summary>
public static class OptiCliMcpExtensions
{
    /// <summary>The authorization policy on the MCP endpoint: a valid access token with the <c>content:read</c> scope.</summary>
    public const string Policy = "OptiCliMcp";

    /// <summary>
    /// Registers the MCP server, its authorization server and its bearer scheme, with options from the
    /// <c>OptiCli:Mcp</c> configuration section and then <paramref name="configure"/>. The site's default
    /// authentication scheme is left as it is: the editor signs in with the site's own login.
    /// </summary>
    public static IServiceCollection AddOptiCliMcp(this IServiceCollection services, Action<OptiCliMcpOptions>? configure = null)
    {
        if (services.Any(s => s.ServiceType == typeof(Marker)))
        {
            throw new InvalidOperationException("AddOptiCliMcp was called twice; call it once, with all the options.");
        }
        services.AddSingleton<Marker>();

        services.AddOptions<OptiCliMcpOptions>()
            .Configure<IConfiguration>(BindSection)
            .Configure(o => configure?.Invoke(o))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OptiCliMcpOptions>, OptionsValidator>();

        services.AddHttpContextAccessor();
        services.AddAntiforgery();
        services.AddDataProtection();
        services.TryAddSingleton(TimeProvider.System);
        // The site's store and role provider; a test (or another CMS) registers its own first.
        services.TryAddSingleton<IOAuthStore, DdsOAuthStore>();
        services.TryAddScoped<IEditorRoles, CmsEditorRoles>();
        services.TryAddSingleton(new OAuthRateLimits());
        services.AddSingleton<OAuthRateLimiter>();
        services.AddSingleton<ClientResolver>();
        services.AddSingleton<GrantCache>();
        services.AddSingleton<TokenService>();
        services.AddSingleton<OAuthMaintenance>();
        services.AddSingleton<McpAudit>();
        services.AddScoped<EditorGate>();
        services.AddScoped<AuthorizationServer>();
        services.AddScoped<ConnectionsPage>();

        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpBearerHandler>(McpBearerHandler.SchemeName, null);
        // Adding a scheme mustn't change which one is the site's default: with a single scheme of its own and no
        // default set, ASP.NET Core would use that one implicitly, and stops doing so once there are two.
        services.PostConfigure<AuthenticationOptions>(o =>
        {
            if (o.DefaultScheme is null && o.Schemes.Where(s => s.Name != McpBearerHandler.SchemeName).ToList() is [var only])
            {
                o.DefaultScheme = only.Name;
            }
        });
        services.AddAuthorization(o => o.AddPolicy(Policy, p => p
            .AddAuthenticationSchemes(McpBearerHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireAssertion(c => Scopes.Split(c.User.FindFirst(McpClaims.Scope)?.Value).Contains(Scopes.Read))));

        services.AddMcpServer(o => o.ServerInfo = new() { Name = "opticli", Version = Version })
            .WithHttpTransport(t => t.Stateless = true)
            .WithRequestFilters(f => f.AddCallToolFilter(ToolAudit.Filter))
            .WithTools<WhoAmITool>();
        return services;
    }

    /// <summary>
    /// Maps <c>{BasePath}/mcp</c>, the OAuth endpoints under <c>{BasePath}/oauth/</c>, the connections page and the
    /// metadata documents. Call it before the CMS's <c>MapContent()</c>, so content routing doesn't see these paths.
    /// </summary>
    public static IEndpointRouteBuilder MapOptiCliMcp(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<OptiCliMcpOptions>>().Value;
        var all = new List<IEndpointConventionBuilder>();

        all.Add(endpoints.MapMcp(options.McpPath).RequireAuthorization(Policy));

        // Everything else is reached before a token exists: anonymous even on a site whose fallback policy requires a
        // login, and the consent and connections pages challenge the site's scheme themselves.
        foreach (var path in McpUrls.ResourceMetadataPaths(options))
        {
            all.Add(endpoints.MapGet(path, Handle(c => Task.FromResult(Server(c).ResourceMetadata(c)))).AllowAnonymous());
        }
        foreach (var path in McpUrls.ServerMetadataPaths(options))
        {
            all.Add(endpoints.MapGet(path, Handle(c => Task.FromResult(Server(c).ServerMetadata(c)))).AllowAnonymous());
        }
        all.Add(endpoints.MapPost(McpUrls.RegisterPath(options), Handle(c => Server(c).Register(c))).AllowAnonymous());
        all.Add(endpoints.MapGet(McpUrls.AuthorizePath(options), Handle(c => Server(c).AuthorizePage(c))).AllowAnonymous());
        all.Add(endpoints.MapPost(McpUrls.AuthorizePath(options), Handle(c => Server(c).AuthorizeConsent(c))).AllowAnonymous());
        all.Add(endpoints.MapPost(McpUrls.TokenPath(options), Handle(c => Server(c).Token(c))).AllowAnonymous());
        all.Add(endpoints.MapGet(McpUrls.ConnectionsPath(options), Handle(c => Connections(c).Show(c))).AllowAnonymous());
        all.Add(endpoints.MapPost(McpUrls.ConnectionsPath(options), Handle(c => Connections(c).Revoke(c))).AllowAnonymous());

        if (options.RequireHost is { Length: > 0 } host)
        {
            foreach (var builder in all)
            {
                builder.RequireHost(host);
            }
        }
        return endpoints;
    }

    /// <summary>A plain request delegate that writes the handler's result: no parameter binding to second-guess.</summary>
    private static RequestDelegate Handle(Func<HttpContext, Task<IResult>> handler) => async context => await (await handler(context)).ExecuteAsync(context);

    private static AuthorizationServer Server(HttpContext context) => context.RequestServices.GetRequiredService<AuthorizationServer>();

    private static ConnectionsPage Connections(HttpContext context) => context.RequestServices.GetRequiredService<ConnectionsPage>();

    /// <summary>
    /// The configuration section, with one difference from plain binding: a role list there replaces the default list
    /// instead of adding to it, so a site can narrow it.
    /// </summary>
    private static void BindSection(OptiCliMcpOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(OptiCliMcpOptions.SectionName);
        var defaultRoles = options.AllowedRoles;
        section.Bind(options);
        var roles = section.GetSection(nameof(OptiCliMcpOptions.AllowedRoles));
        options.AllowedRoles = roles.Exists() ? roles.Get<string[]>() ?? [] : defaultRoles;
    }

    private static string Version =>
        typeof(OptiCliMcpExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Gives the actual problem in the error, not just "invalid".</summary>
    private sealed class OptionsValidator : IValidateOptions<OptiCliMcpOptions>
    {
        public ValidateOptionsResult Validate(string? name, OptiCliMcpOptions options) =>
            options.Problem() is { } problem ? ValidateOptionsResult.Fail($"OptiCli:Mcp: {problem}") : ValidateOptionsResult.Success;
    }

    private sealed class Marker;
}
