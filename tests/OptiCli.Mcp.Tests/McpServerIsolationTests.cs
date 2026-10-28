using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.Support;

namespace OptiCli.Mcp.Tests;

/// <summary>
/// Another MCP server in the same site (Optimizely's own on CMS 13, Optimizely.Cms.Opal.Tools, or any package on the
/// same SDK): the SDK shares one set of server options, so each endpoint must still serve only its own server.
/// </summary>
public sealed class McpServerIsolationTests
{
    /// <summary>The other server: a tool, a prompt, a filter, instructions and a session hook of its own, on /other-mcp.</summary>
    private static void OtherServer(IServiceCollection services, List<string> otherFilter, List<string> otherHook) =>
        services.AddMcpServer(o => (o.ServerInfo, o.ServerInstructions) = (new() { Name = "other", Version = "1.0" }, "The other server's instructions."))
            .WithHttpTransport(t => t.ConfigureSessionOptions = (context, _, _) =>
            {
                otherHook.Add(context.Request.Path);
                return Task.CompletedTask;
            })
            .WithRequestFilters(f => f.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                otherFilter.Add(context.Params!.Name);
                return await next(context, cancellationToken);
            }))
            .WithTools<OtherTools>()
            .WithPrompts<OtherPrompts>();

    /// <summary>Whether calling <paramref name="tool"/> fails: an error result, or the SDK's refusal of an unknown tool.</summary>
    private static async Task<bool> Fails(McpClient client, string tool)
    {
        try
        {
            return (await client.CallToolAsync(tool, new Dictionary<string, object?> { ["text"] = "x" })).IsError == true;
        }
        catch (ModelContextProtocol.McpException)
        {
            return true;
        }
    }

    [Fact]
    public async Task The_modules_endpoint_serves_only_the_module_and_the_other_endpoint_only_the_other_server()
    {
        var otherFilter = new List<string>();
        var otherHook = new List<string>();
        await using var site = await TestSite.StartAsync(o => o.AllowDelete = true,
            services: services => OtherServer(services, otherFilter, otherHook), endpoints: app => app.MapMcp("/other-mcp"));

        await using (var ours = await ToolTests.Connect(site))
        {
            var tools = (await ours.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.Contains("whoami", tools);
            Assert.Contains("delete_content", tools);
            Assert.DoesNotContain("other_echo", tools);
            Assert.Equal(("opticli", OptiCliMcpExtensions.Version), (ours.ServerInfo.Name, ours.ServerInfo.Version));
            Assert.Contains("baseVersion", ours.ServerInstructions);
            Assert.Null(ours.ServerCapabilities.Prompts);
            Assert.True(await Fails(ours, "other_echo"));
            await ours.CallToolAsync("whoami", new Dictionary<string, object?>());
        }
        // Neither the other server's filter nor its session hook ran for the module's requests.
        Assert.Empty(otherFilter);
        Assert.Empty(otherHook);
        Assert.Contains(site.Audit.Audit, m => m.Contains("MCP tool whoami ok"));

        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/other-mcp") }, site.Client(), ownsHttpClient: true);
        await using (var others = await McpClient.CreateAsync(transport))
        {
            Assert.Equal(["other_echo"], (await others.ListToolsAsync()).Select(t => t.Name));
            Assert.Equal("other", others.ServerInfo.Name);
            Assert.Equal("The other server's instructions.", others.ServerInstructions);
            var result = await others.CallToolAsync("other_echo", new Dictionary<string, object?> { ["text"] = "hi" });
            Assert.Equal("hi", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.True(await Fails(others, "whoami"));
        }
        Assert.Equal("other_echo", otherFilter[0]);
        Assert.All(otherHook, path => Assert.Equal("/other-mcp", path));
        // The module audits only its own endpoint's calls: the editor's try at other_echo there, not the other server's call.
        Assert.Single(site.Audit.Audit, m => m.Contains("MCP tool other_echo"));
        Assert.Contains(site.Audit.Audit, m => m.Contains("MCP tool other_echo failed: user editor"));
    }

    [Fact]
    public async Task A_tool_is_the_modules_by_the_assembly_of_its_method()
    {
        Assert.True(McpServerIsolation.IsModules(McpServerTool.Create(typeof(Tools.WhoAmITool).GetMethods().First(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false).Length > 0), _ => null!)));
        Assert.False(McpServerIsolation.IsModules(McpServerTool.Create(typeof(OtherTools).GetMethod(nameof(OtherTools.Echo))!)));
    }

    private const string Mcp = "/episerver/opticli/mcp";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    private const string ListTools = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private static string Call(string tool) => "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{}}}";

    /// <summary>
    /// The site's own anonymous MCP server, registered after the module as a site adds one: its transport settings are
    /// the application's, so it makes the module's endpoint stateful too (and maps legacy SSE there when asked).
    /// </summary>
    private static void SiteServer(IServiceCollection services, bool legacySse = false) =>
        services.AddMcpServer()
            .WithHttpTransport(t =>
            {
                t.Stateless = false;
#pragma warning disable MCP9004 // Obsolete: what a site that still serves old clients sets.
                t.EnableLegacySse = legacySse;
#pragma warning restore MCP9004
            })
            .WithTools<SiteTools>();

    /// <summary>One JSON-RPC message posted as an MCP client does, with a bearer token and a session when given.</summary>
    private static async Task<(HttpStatusCode Status, string? Session, string Body)> Rpc(TestSite site, string path, string body, string? token = null, string? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (session is not null)
        {
            request.Headers.Add("Mcp-Session-Id", session);
        }
        using var response = await site.Client().SendAsync(request);
        return (response.StatusCode, response.Headers.TryGetValues("Mcp-Session-Id", out var id) ? id.Single() : null, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The module's endpoint with the SDK's own client: only the module's tools, and they run as the editor.</summary>
    private static async Task ServesOnlyTheModule(TestSite site)
    {
        await using var ours = await ToolTests.Connect(site);
        var tools = (await ours.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("whoami", tools);
        Assert.DoesNotContain("site_whoami", tools);
        var whoami = await ours.CallToolAsync("whoami", new Dictionary<string, object?>());
        Assert.NotEqual(true, whoami.IsError);
        Assert.Contains("\"name\":\"editor\"", Assert.IsType<TextContentBlock>(Assert.Single(whoami.Content)).Text);
    }

    [Fact]
    public async Task With_a_stateful_site_server_a_session_serves_only_its_own_connection_at_its_own_endpoint()
    {
        await using var site = await TestSite.StartAsync(after: services => SiteServer(services), endpoints: app => app.MapMcp("/site-mcp"));
        await ServesOnlyTheModule(site);
        var editor = await site.ConnectAsync();
        // The same editor's second connection: another grant.
        var second = await site.ConnectAsync();

        var ours = await Rpc(site, Mcp, Initialize, editor.Access);
        Assert.Equal(HttpStatusCode.OK, ours.Status);
        Assert.NotNull(ours.Session);
        var theirs = await Rpc(site, "/site-mcp", Initialize);
        Assert.Equal(HttpStatusCode.OK, theirs.Status);
        Assert.NotNull(theirs.Session);

        // The site's session at the module's endpoint, with the editor's token: never the site's tools as the editor. Not
        // a session the module made (and the SDK's own check would refuse it too: it was made anonymously).
        foreach (var body in new[] { ListTools, Call("site_whoami") })
        {
            var crossed = await Rpc(site, Mcp, body, editor.Access, theirs.Session);
            Assert.Equal(HttpStatusCode.NotFound, crossed.Status);
            Assert.DoesNotContain("site_whoami", crossed.Body);
            Assert.DoesNotContain("editor", crossed.Body);
        }
        // The module's session at the site's endpoint, anonymously and with the editor's token (which that endpoint
        // doesn't sign in): never the module's server. The SDK refuses it: the session is bound to the connection.
        foreach (var token in new[] { null, editor.Access })
        {
            foreach (var body in new[] { ListTools, Call("whoami") })
            {
                var crossed = await Rpc(site, "/site-mcp", body, token, ours.Session);
                Assert.Equal(HttpStatusCode.Forbidden, crossed.Status);
                Assert.DoesNotContain("get_content", crossed.Body);
                Assert.DoesNotContain("editor", crossed.Body);
            }
        }
        // The module's session by another connection at the module's endpoint.
        var stolen = await Rpc(site, Mcp, Call("whoami"), second.Access, ours.Session);
        Assert.Equal(HttpStatusCode.NotFound, stolen.Status);
        Assert.DoesNotContain("editor", stolen.Body);
        // A session nobody made.
        Assert.Equal(HttpStatusCode.NotFound, (await Rpc(site, Mcp, ListTools, editor.Access, "not-a-session")).Status);

        // Its own connection keeps it.
        var whoami = await Rpc(site, Mcp, Call("whoami"), editor.Access, ours.Session);
        Assert.Equal(HttpStatusCode.OK, whoami.Status);
        Assert.Contains("editor", whoami.Body);
        Assert.DoesNotContain("\"isError\":true", whoami.Body);
        // And the site's server serves the site.
        var siteCall = await Rpc(site, "/site-mcp", Call("site_whoami"), session: theirs.Session);
        Assert.Contains("anonymous", siteCall.Body);
    }

    [Fact]
    public async Task With_legacy_sse_on_the_site_the_modules_endpoint_has_none_and_no_message_runs_as_the_editor()
    {
        await using var site = await TestSite.StartAsync(after: services => SiteServer(services, legacySse: true), endpoints: app => app.MapMcp("/site-mcp"));
        await ServesOnlyTheModule(site);
        var editor = await site.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var open = new HttpRequestMessage(HttpMethod.Get, Mcp + "/sse");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", editor.Access);
        open.Headers.Accept.ParseAdd("text/event-stream");
        using var sse = await site.Client().SendAsync(open, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (sse.IsSuccessStatusCode)
        {
            // An editor's SSE session at the module: what anyone holding its ID could do with it at the site's endpoint.
            var reader = new StreamReader(await sse.Content.ReadAsStreamAsync(timeout.Token));
            var endpoint = await Data(reader, timeout.Token);
            var session = endpoint[(endpoint.IndexOf("sessionId=", StringComparison.Ordinal) + "sessionId=".Length)..];
            var seen = new List<string>();
            foreach (var body in new[] { Initialize, Call("whoami") })
            {
                var posted = await site.Client().PostAsync("/site-mcp/message?sessionId=" + session, new StringContent(body, Encoding.UTF8, "application/json"), timeout.Token);
                if (posted.IsSuccessStatusCode)
                {
                    seen.Add(await Data(reader, timeout.Token));
                }
            }
            Assert.DoesNotContain(seen, message => message.Contains("editor"));
        }
        Assert.Equal(HttpStatusCode.NotFound, sse.StatusCode);

        // Nor the message route, nor a GET stream: the module serves Streamable HTTP POSTs only.
        var message = new HttpRequestMessage(HttpMethod.Post, Mcp + "/message?sessionId=x") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", editor.Access);
        Assert.Equal(HttpStatusCode.NotFound, (await site.Client().SendAsync(message)).StatusCode);
        var stream = new HttpRequestMessage(HttpMethod.Get, Mcp);
        stream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", editor.Access);
        stream.Headers.Accept.ParseAdd("text/event-stream");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await site.Client().SendAsync(stream)).StatusCode);

        // The site's own legacy SSE still works for the site.
        var siteOpen = new HttpRequestMessage(HttpMethod.Get, "/site-mcp/sse");
        siteOpen.Headers.Accept.ParseAdd("text/event-stream");
        using var siteSse = await site.Client().SendAsync(siteOpen, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, siteSse.StatusCode);
        Assert.Contains("/site-mcp/message?sessionId=", await Data(new StreamReader(await siteSse.Content.ReadAsStreamAsync(timeout.Token)), timeout.Token));
    }

    [Fact]
    public async Task A_session_hook_replaced_after_the_module_turns_its_endpoint_off_with_an_error_in_the_log()
    {
        var otherFilter = new List<string>();
        var otherHook = new List<string>();
        await using var site = await TestSite.StartAsync(
            after: services =>
            {
                OtherServer(services, otherFilter, otherHook);
                // A library that sets the hook in a PostConfigure of its own, after the module's: the module's never runs.
                services.PostConfigure<HttpServerTransportOptions>(t => t.ConfigureSessionOptions = (_, _, _) => Task.CompletedTask);
            },
            endpoints: app => app.MapMcp("/other-mcp"));
        var editor = await site.ConnectAsync();

        var refused = await Rpc(site, Mcp, Initialize, editor.Access);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.Status);
        Assert.DoesNotContain("other_echo", refused.Body);
        Assert.Contains("The site's log says why.", System.Text.Json.JsonDocument.Parse(refused.Body).RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Contains(site.Audit.Entries, e => e.Message.Contains("ConfigureSessionOptions") && e.Message.Contains("refuses"));
        // Still the 401 that leads a client to sign in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(site, Mcp, Initialize)).Status);
    }

    [Fact]
    public async Task The_editors_principal_binds_a_session_to_its_connection_by_the_claim_the_sdk_compares()
    {
        await using var site = await TestSite.StartAsync();
        var tokens = await site.ConnectAsync();
        var editor = await site.Services.GetRequiredService<TokenService>().ValidateAsync(tokens.Access, "http://localhost/episerver/opticli/mcp", default);
        var grant = editor!.FindFirst(McpClaims.Grant)!.Value;
        var subject = Assert.Single(editor.FindAll(TokenService.SessionSubject));
        Assert.Equal((grant, TokenService.AuthenticationType), (subject.Value, subject.Issuer));
        // The SDK takes the first of these it finds: none comes before the grant.
        Assert.Null(editor.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier));
        Assert.Null(editor.FindFirst(System.Security.Claims.ClaimTypes.Upn));
        Assert.Equal("editor", editor.Identity!.Name);
        // A preview link's principal, which the site's own pages see, has none.
        var stored = (await site.Store.FindGrantAsync(grant, default))!;
        Assert.Null(TokenService.Principal(stored, authenticationType: "OptiCliPreview").FindFirst(TokenService.SessionSubject));
    }

    [Fact]
    public async Task The_modules_server_takes_messages_from_its_own_connection_only()
    {
        await using var server = McpServer.Create(new StreamableHttpServerTransport(), new McpServerOptions());
        var called = 0;
        Task Next(MessageContext context, CancellationToken cancellationToken)
        {
            called++;
            return Task.CompletedTask;
        }
        System.Security.Claims.ClaimsPrincipal Of(string grant, string type = TokenService.AuthenticationType) => EditorGate.Principal("editor", ["WebEditors"], type, [new(McpClaims.Grant, grant)]);
        Task Send(string? bound, System.Security.Claims.ClaimsPrincipal? user) =>
            McpServerIsolation.BoundTo(bound)(Next)(new MessageContext(server, new JsonRpcRequest { Method = "tools/list", Context = new() { User = user } }), default);

        await Send("g1", Of("g1"));
        Assert.Equal(1, called);
        foreach (var (bound, user) in new (string?, System.Security.Claims.ClaimsPrincipal?)[]
                 {
                     ("g1", Of("g2")), ("g1", null), ("g1", new()), ("g1", Of("g1", "Cookies")), (null, null), (null, Of("g1")),
                 })
        {
            var refused = await Assert.ThrowsAsync<McpProtocolException>(() => Send(bound, user));
            Assert.Equal(McpErrorCode.InvalidRequest, refused.ErrorCode);
        }
        Assert.Equal(1, called);
    }

    [Fact]
    public void The_modules_tools_run_only_in_a_server_the_modules_isolation_made()
    {
        var grant = new Grant
        {
            GrantId = "g", ClientId = "c", ClientName = "C", UserName = "editor", Roles = ["WebEditors"], Scope = "content:read",
            Resource = "r", RefreshHash = "h", Created = DateTimeOffset.UnixEpoch, Expires = DateTimeOffset.UnixEpoch,
        };
        var context = new DefaultHttpContext { User = TokenService.Principal(grant) };
        var accessor = new HttpContextAccessor { HttpContext = context };
        // No MCP server (a request the SDK didn't handle), and one the module didn't make (its hook didn't run).
        Assert.Throws<ModelContextProtocol.McpException>(() => Tools.McpEditor.From(accessor));
        context.Features.Set(McpServer.Create(new StreamableHttpServerTransport(), new McpServerOptions()));
        Assert.Throws<ModelContextProtocol.McpException>(() => Tools.McpEditor.From(accessor));
        context.Features.Set(McpServer.Create(new StreamableHttpServerTransport(), McpServerIsolation.Marked(new McpServerOptions())));
        Assert.Equal("editor", Tools.McpEditor.From(accessor).Name);
    }

    [Fact]
    public void The_modules_sessions_are_its_own_connections_and_idle_ones_are_forgotten()
    {
        var time = new ManualTime();
        var sessions = new McpSessions(time, Microsoft.Extensions.Options.Options.Create(new HttpServerTransportOptions()));
        sessions.Made("s1", "g1");
        Assert.True(sessions.Owns("s1", "g1"));
        Assert.False(sessions.Owns("s1", "g2"));
        Assert.False(sessions.Owns("s1", null));
        Assert.False(sessions.Owns("s2", "g1"));
        sessions.Ended("s1");
        Assert.False(sessions.Owns("s1", "g1"));

        for (var i = 0; i < McpSessions.PruneAbove; i++)
        {
            sessions.Made($"old{i}", "g1");
        }
#pragma warning disable MCP9006
        time.Advance(new HttpServerTransportOptions().IdleTimeout + TimeSpan.FromMinutes(1));
#pragma warning restore MCP9006
        Assert.True(sessions.Owns("old0", "g1"));
        sessions.Made("new", "g1");
        Assert.Equal(2, sessions.Count);
        Assert.True(sessions.Owns("old0", "g1"));
        Assert.False(sessions.Owns("old1", "g1"));
    }

    [Fact]
    public void Sessions_made_in_a_loop_are_kept_to_the_sdks_limit_least_recently_used_first()
    {
        var time = new ManualTime();
#pragma warning disable MCP9006
        var sessions = new McpSessions(time, Microsoft.Extensions.Options.Options.Create(new HttpServerTransportOptions { MaxIdleSessionCount = 1500 }));
#pragma warning restore MCP9006
        // None idle: one a second, for well under the SDK's idle timeout.
        for (var i = 0; i < 2000; i++)
        {
            sessions.Made($"s{i}", "g1");
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(2000, sessions.Count);
        Assert.True(sessions.Owns("s0", "g1"));

        sessions.Made("new", "g1");

        Assert.Equal(1501, sessions.Count);
        Assert.True(sessions.Owns("s0", "g1"));
        Assert.False(sessions.Owns("s1", "g1"));
        Assert.False(sessions.Owns("s500", "g1"));
        Assert.True(sessions.Owns("s501", "g1"));
        Assert.True(sessions.Owns("new", "g1"));

        // However many are made, never more than twice the limit.
        for (var i = 0; i < 20_000; i++)
        {
            sessions.Made($"loop{i}", "g1");
            Assert.True(sessions.Count <= 3000);
        }
    }

    /// <summary>The next server-sent event's data.</summary>
    private static async Task<string> Data(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                return line["data:".Length..].Trim();
            }
        }
        throw new EndOfStreamException();
    }

    [McpServerToolType]
    public sealed class SiteTools
    {
        [McpServerTool(Name = "site_whoami"), Description("Who the request is signed in as.")]
        public static string WhoAmI(IHttpContextAccessor http) => http.HttpContext?.User.Identity is { IsAuthenticated: true, Name: { } name } ? name : "anonymous";
    }

    [McpServerToolType]
    public sealed class OtherTools
    {
        [McpServerTool(Name = "other_echo"), Description("Echoes.")]
        public static string Echo(string text) => text;
    }

    [McpServerPromptType]
    public sealed class OtherPrompts
    {
        [McpServerPrompt(Name = "other_prompt"), Description("A prompt.")]
        public static string Prompt() => "Say hi.";
    }
}
