using System.Reflection;
using EPiServer.Web.Routing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using OptiCli.Cms.Content;
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
        services.AddSingleton(sp => new OAuthRateLimiter(OAuthRateLimits.From(sp.GetRequiredService<IOptions<OptiCliMcpOptions>>().Value.RateLimits)));
        services.AddSingleton<ClientResolver>();
        services.AddSingleton<GrantCache>();
        services.AddSingleton<TokenService>();
        services.AddSingleton<OAuthMaintenance>();
        services.AddSingleton<McpAudit>();
        services.AddScoped<EditorGate>();
        // What the CMS edit UI shows an editor of each content item, for the content operations (EditUiProperties).
        services.AddScoped<IEditUiMetadata, CmsUiMetadata>();
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
            // The audit outside, so it records the error results the inner filter makes of refusals.
            .WithRequestFilters(f => f.AddCallToolFilter(ToolAudit.Filter).AddCallToolFilter(ToolErrors.Filter))
            .WithTools<WhoAmITool>()
            .WithTools<ReadTools>()
            .WithTools<WriteTools>()
            .WithTools<PublishTools>()
            .WithTools<DeleteTool>();
        // What the site allows is only known once its options are: the workflow the model is told, and whether deleting
        // is offered at all (the tool also checks, should it be called regardless).
        services.AddOptions<McpServerOptions>().PostConfigure<IOptions<OptiCliMcpOptions>>((mcp, ours) =>
        {
            mcp.ServerInstructions = McpInstructions.For(ours.Value);
            if (!ours.Value.AllowDelete && mcp.ToolCollection?.TryGetPrimitive(DeleteTool.ToolName, out var delete) == true)
            {
                mcp.ToolCollection.Remove(delete);
            }
        });
        return services;
    }

    /// <summary>
    /// Maps <c>{BasePath}/mcp</c>, the OAuth endpoints under <c>{BasePath}/oauth/</c>, the connections page and the
    /// metadata documents. Call it after the CMS's <c>MapContent()</c> and the site's own endpoints: its paths are
    /// literal, so they win over content routing's catch-all wherever they are mapped.
    /// </summary>
    /// <remarks>
    /// Not before <c>MapContent()</c>: with any endpoint mapped before it, the CMS freezes the routes add-ons register
    /// through <c>IEndpointRoutingExtension</c> (its documentation asks for other endpoints before or after it, not both),
    /// and a <c>MapControllers()</c> after it then maps the site's controllers a second time, so that a named route fails
    /// every request with "Duplicate endpoint name". Mapped after everything else, the module changes nothing for the rest.
    /// </remarks>
    public static IEndpointRouteBuilder MapOptiCliMcp(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<OptiCliMcpOptions>>().Value;
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OptiCliMcpExtensions).FullName!);
        if (endpoints.DataSources.Count == 0 && endpoints.ServiceProvider.GetServices<IEndpointRoutingExtension>().Any())
        {
            logger.LogWarning(
                "MapOptiCliMcp() is the site's first endpoint mapping. Mapped before MapContent(), it makes a MapControllers() after MapContent() map the site's controllers twice (\"Duplicate endpoint name\"): call MapOptiCliMcp() after MapContent() and the site's own endpoints.");
        }
        if (options.AllowedRedirectHosts.Contains(OptiCliMcpOptions.AnyRedirectHost))
        {
            logger.LogWarning(
                "OptiCli:Mcp:AllowedRedirectHosts is [\"*\"]: any app that registers itself may have the codes editors approve sent to any https host it names. Only do this for a site that knowingly accepts web apps other than Claude; list their hosts instead where you can.");
        }
        var all = new List<IEndpointConventionBuilder>();

        var mcp = endpoints.MapMcp(options.McpPath).RequireAuthorization(Policy);
        // Inside authorization, so an anonymous request still gets its 401 and the way to a token, whatever its size.
        mcp.Add(endpoint => endpoint.RequestDelegate = McpRequestLimit.Wrap(endpoint.RequestDelegate!, McpRequestLimit.Bytes(options), options.MaxUploadBytes));
        all.Add(mcp);

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

        foreach (var builder in all)
        {
            // Last, so it wraps the others: the site's status code pages never rewrite the module's errors.
            builder.Add(endpoint =>
            {
                if (endpoint.RequestDelegate is { } next)
                {
                    endpoint.RequestDelegate = StatusCodePages.Skipping(next);
                }
            });
            if (options.RequireHost is { Length: > 0 } host)
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
    /// The configuration section, with one difference from plain binding: a role or redirect host list there replaces the
    /// default list instead of adding to it, so a site can narrow it.
    /// </summary>
    private static void BindSection(OptiCliMcpOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(OptiCliMcpOptions.SectionName);
        var defaultRoles = options.AllowedRoles;
        var defaultHosts = options.AllowedRedirectHosts;
        section.Bind(options);
        options.AllowedRoles = Replacing(section, nameof(OptiCliMcpOptions.AllowedRoles), defaultRoles);
        options.AllowedRedirectHosts = Replacing(section, nameof(OptiCliMcpOptions.AllowedRedirectHosts), defaultHosts);
    }

    /// <summary>The list in <paramref name="section"/>'s <paramref name="key"/>, if it has one; otherwise the default.</summary>
    private static string[] Replacing(IConfigurationSection section, string key, string[] defaults) =>
        section.GetSection(key) is { } list && list.Exists() ? list.Get<string[]>() ?? [] : defaults;

    /// <summary>The package's version (Directory.Build.props), as the server's MCP <c>serverInfo</c>; without build metadata.</summary>
    internal static string Version =>
        typeof(OptiCliMcpExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Gives the actual problem in the error, not just "invalid".</summary>
    private sealed class OptionsValidator : IValidateOptions<OptiCliMcpOptions>
    {
        public ValidateOptionsResult Validate(string? name, OptiCliMcpOptions options) =>
            options.Problem() is { } problem ? ValidateOptionsResult.Fail($"OptiCli:Mcp: {problem}") : ValidateOptionsResult.Success;
    }

    private sealed class Marker;
}
