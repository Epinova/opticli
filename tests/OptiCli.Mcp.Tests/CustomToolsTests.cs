using System.ComponentModel;
using System.Text.Json;
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
using OptiCli.Mcp.Tools;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tests;

/// <summary>
/// A site's own tools on the module's server (<see cref="IOptiCliMcpBuilder"/>): registered, checked, served only on the
/// module's endpoint, run as the editor, and failing with the module's errors. Without a CMS: the tools here read
/// nothing from content.
/// </summary>
public sealed class CustomToolsTests
{
    private const string SiteMcp = "http://localhost/site-mcp";

    private static void Custom(IOptiCliMcpBuilder module) => module.WithTools<CustomReadTools>();

    private static async Task<McpClient> SiteClient(TestSite site) =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(SiteMcp) }, site.Client(), ownsHttpClient: true));

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static async Task<CallToolResult> Call(McpClient client, string tool, object arguments) =>
        await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments)));

    private static AgentError Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        var text = Text(result);
        // Just the JSON, as the module's own tools send it: not the SDK's "An error occurred invoking ..." text.
        Assert.StartsWith("{", text, StringComparison.Ordinal);
        return JsonSerializer.Deserialize<AgentError>(text, AgentJson.Options)!;
    }

    [Fact]
    public async Task A_sites_read_tool_is_offered_beside_the_modules_and_runs_as_the_editor()
    {
        await using var site = await TestSite.StartAsync(module: Custom);
        await using var client = await ToolTests.Connect(site);

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        Assert.Contains("whoami", tools.Keys);
        Assert.Contains("get_content", tools.Keys);
        var whoami = tools["custom_whoami"];
        Assert.Equal("Custom: who am I", whoami.Title);
        Assert.Equal("Who the site's tool runs for.", whoami.Description);
        Assert.Equal((true, false, true, false), (whoami.Annotations!.ReadOnlyHint, whoami.Annotations.DestructiveHint, whoami.Annotations.IdempotentHint, whoami.Annotations.OpenWorldHint));
        // Its own argument, not the editor service it takes.
        var properties = whoami.InputSchema.GetProperty("properties");
        Assert.Equal("Echoed back.", properties.GetProperty("text").GetProperty("description").GetString());
        Assert.False(properties.TryGetProperty("editor", out _));
        Assert.Contains("custom_fail", tools.Keys);

        var ran = JsonDocument.Parse(Text(await Call(client, "custom_whoami", new { text = "hi" }))).RootElement;
        Assert.Equal("editor", ran.GetProperty("editor").GetString());
        Assert.Equal("hi", ran.GetProperty("text").GetString());
        Assert.True(ran.GetProperty("write").GetBoolean());
        Assert.Contains(Scopes.Read, ran.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool custom_whoami ok: user editor", StringComparison.Ordinal));

        // A connection the editor allowed reading only: the tool still runs (the endpoint requires content:read), and sees it.
        await using var reading = await ToolTests.Connect(site, scope: Scopes.Read);
        var readOnly = JsonDocument.Parse(Text(await Call(reading, "custom_whoami", new { text = "x" }))).RootElement;
        Assert.False(readOnly.GetProperty("write").GetBoolean());
        Assert.Equal([Scopes.Read], readOnly.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task A_sites_tool_reads_as_the_editor_also_where_the_sites_own_server_makes_the_endpoint_stateful()
    {
        // A stateful server gets the application's services and a DI scope per MCP request, not the HTTP request's.
        await using var site = await TestSite.StartAsync(module: Custom,
            after: services => services.AddMcpServer().WithHttpTransport(t => t.Stateless = false).WithTools<SiteServerTools>(),
            endpoints: app => app.MapMcp("/site-mcp"));
        await using var client = await ToolTests.Connect(site);

        var ran = JsonDocument.Parse(Text(await Call(client, "custom_whoami", new { text = "stateful" }))).RootElement;
        Assert.Equal("editor", ran.GetProperty("editor").GetString());
        Assert.Equal(AgentErrorCodes.NotFound, Error(await Call(client, "custom_fail", new { kind = "tool" })).Code);
    }

    [Fact]
    public async Task Without_custom_tools_the_module_offers_and_says_exactly_what_it_did()
    {
        await using var site = await TestSite.StartAsync();
        await using var client = await ToolTests.Connect(site);

        Assert.DoesNotContain(await client.ListToolsAsync(), t => t.Name.StartsWith("custom_", StringComparison.Ordinal));
        Assert.Equal(McpInstructions.For(site.Options), client.ServerInstructions);
        Assert.DoesNotContain(McpInstructions.SiteHeading, client.ServerInstructions);
        Assert.DoesNotContain(site.Services.GetRequiredService<ModuleTools>().All, e => e.Custom);
    }

    [Fact]
    public async Task The_module_makes_its_own_tools_exactly_as_the_SDK_made_them()
    {
        // What AddOptiCliMcp registered before the module made its tools itself.
        var sdk = new ServiceCollection();
        sdk.AddMcpServer().WithTools<WhoAmITool>().WithTools<ReadTools>().WithTools<WriteTools>().WithTools<PublishTools>().WithTools<DeleteTool>();
        await using var provider = sdk.BuildServiceProvider();
        var expected = provider.GetServices<McpServerTool>().Select(Protocol).ToList();

        await using var site = await TestSite.StartAsync(module: Custom);
        var made = site.Services.GetRequiredService<ModuleTools>().All.Where(e => !e.Custom).Select(e => e.Tool).ToList();

        Assert.Equal(expected, made.Select(Protocol));
        Assert.All(made, tool => Assert.True(McpServerIsolation.IsModules(tool)));
        Assert.Equal(CustomTools.BuiltInNames.Order(), made.Select(t => t.ProtocolTool.Name).Order());
        Assert.Contains(DeleteTool.ToolName, CustomTools.BuiltInNames);
    }

    /// <summary>Everything a client sees of a tool: name, title, description, schema, annotations.</summary>
    private static string Protocol(McpServerTool tool) => JsonSerializer.Serialize(tool.ProtocolTool, McpJsonUtilities.DefaultOptions);

    [Fact]
    public async Task The_sites_instructions_come_after_the_modules_under_a_heading_of_their_own()
    {
        await using var site = await TestSite.StartAsync(module: m => m
            .WithInstructions("Product pages live below Products; search them with custom_whoami.")
            .WithInstructions("  Prices are in NOK.  "));
        await using var client = await ToolTests.Connect(site);

        Assert.Equal(
            McpInstructions.For(site.Options) + "\n\n" + McpInstructions.SiteHeading + "\nProduct pages live below Products; search them with custom_whoami.\nPrices are in NOK.",
            client.ServerInstructions);
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddOptiCliMcp().WithInstructions(" "));
    }

    [Fact]
    public async Task A_sites_tool_fails_with_the_modules_error_json()
    {
        await using var site = await TestSite.StartAsync(module: Custom);
        await using var client = await ToolTests.Connect(site);

        var tool = Error(await Call(client, "custom_fail", new { kind = "tool" }));
        Assert.Equal((AgentErrorCodes.NotFound, "No product called that.", "Search with other words."), (tool.Code, tool.Message, tool.Hint));

        // Arguments that don't bind: the assistant's to fix.
        var missing = Error(await Call(client, "custom_fail", new { count = 1 }));
        Assert.Equal(AgentErrorCodes.Usage, missing.Code);
        Assert.Contains("'kind'", missing.Message);
        Assert.Equal("Check the tool's input schema.", missing.Hint);
        var wrongType = Error(await Call(client, "custom_fail", new { kind = "x", count = "many" }));
        Assert.Equal(AgentErrorCodes.Usage, wrongType.Code);
        Assert.Contains("'count'", wrongType.Message);
        Assert.Equal(AgentErrorCodes.Usage, Error(await Call(client, "custom_fail", new { kind = new { nested = true } })).Code);

        // What nobody expected: logged with the tool's name, and the client learns only where the details are. Also an
        // ArgumentException or JsonException of the tool's own, with arguments that bound.
        foreach (var kind in new[] { "invalid", "argument", "json" })
        {
            var failed = Error(await Call(client, "custom_fail", new { kind }));
            Assert.Equal(AgentErrorCodes.Internal, failed.Code);
            Assert.DoesNotContain("secret", failed.Message + failed.Hint);
            Assert.DoesNotContain("own", failed.Message + failed.Hint);
        }
        Assert.Equal(3, site.Audit.Entries.Count(e => e.Category == ToolErrors.LogCategory && e.Message == "The site's MCP tool custom_fail failed."));

        // An McpException is the SDK's way for a tool to send its own message: as the SDK sends it.
        var mcp = await Call(client, "custom_fail", new { kind = "mcp" });
        Assert.True(mcp.IsError);
        Assert.Equal("An error occurred invoking 'custom_fail': An McpException's message.", Text(mcp));

        Assert.Equal("ok", Text(await Call(client, "custom_fail", new { kind = "none" })));
        Assert.Equal(8, site.Audit.Audit.Count(m => m.StartsWith("MCP tool custom_fail failed: user editor", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("withTools", false, false)]
    [InlineData("withTools", false, true)]
    [InlineData("withTools", true, false)]
    [InlineData("withTools", true, true)]
    [InlineData("fromAssembly", false, false)]
    [InlineData("fromAssembly", false, true)]
    [InlineData("fromAssembly", true, false)]
    [InlineData("fromAssembly", true, true)]
    public async Task A_sites_tool_class_never_runs_at_another_mcp_endpoint_even_when_that_server_registers_it_too(string how, bool siteFirst, bool stateful)
    {
        void SiteServer(IServiceCollection services)
        {
            var server = services.AddMcpServer().WithHttpTransport(t => t.Stateless = true).WithTools<SiteServerTools>();
            // The site's own server registers the module's classes as well: by name, or every [McpServerToolType] class.
            _ = how == "withTools"
                ? server.WithTools<PlainCustomTools>().WithTools<AttributedCustomTools>()
                : server.WithToolsFromAssembly(typeof(AttributedCustomTools).Assembly);
        }
        await using var site = await TestSite.StartAsync(
            module: m => m.WithTools<PlainCustomTools>().WithTools<AttributedCustomTools>(),
            services: siteFirst ? SiteServer : null,
            after: services =>
            {
                if (!siteFirst)
                {
                    SiteServer(services);
                }
                if (stateful)
                {
                    // Last, so it wins over the module's and the site's Stateless = true.
                    services.Configure<HttpServerTransportOptions>(t => t.Stateless = false);
                }
            },
            endpoints: app => app.MapMcp("/site-mcp"));
        Assert.Equal(stateful, !site.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HttpServerTransportOptions>>().Value.Stateless);

        await using (var ours = await ToolTests.Connect(site))
        {
            var mine = (await ours.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.Contains("plain_custom", mine);
            Assert.Contains("attributed_custom", mine);
            Assert.DoesNotContain("site_editor", mine);
            Assert.Equal("ran as editor", Text(await ours.CallToolAsync("plain_custom", new Dictionary<string, object?>())));
            Assert.Equal("ran as editor", Text(await ours.CallToolAsync("attributed_custom", new Dictionary<string, object?>())));
        }

        await using var theirs = await SiteClient(site);
        var names = (await theirs.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("site_editor", names);
        Assert.DoesNotContain("plain_custom", names);
        Assert.DoesNotContain("attributed_custom", names);
        await Assert.ThrowsAnyAsync<McpException>(() => theirs.CallToolAsync("plain_custom", new Dictionary<string, object?>()).AsTask());
        await Assert.ThrowsAnyAsync<McpException>(() => theirs.CallToolAsync("attributed_custom", new Dictionary<string, object?>()).AsTask());
        // The editor service at another endpoint: refused, as for the module's own tools.
        var editor = await theirs.CallToolAsync("site_editor", new Dictionary<string, object?>());
        Assert.True(editor.IsError);
        Assert.Contains("only runs on the MCP endpoint", Text(editor));
        // Only the editor's calls at the module's endpoint ran them, and only those were audited.
        Assert.Equal(2, site.Audit.Audit.Count(m => m.Contains("_custom ok: user editor")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sites_own_mcp_tool_named_like_a_built_in_one_stays_the_sites_whichever_is_registered_first(bool siteFirst)
    {
        static void SiteServer(IServiceCollection services) =>
            services.AddMcpServer().WithHttpTransport(t => t.Stateless = true).WithTools<ClashingSiteTools>();
        await using var site = await TestSite.StartAsync(
            services: siteFirst ? SiteServer : null,
            after: siteFirst ? null : SiteServer,
            endpoints: app => app.MapMcp("/site-mcp"));

        await using (var theirs = await SiteClient(site))
        {
            Assert.Equal(["site_only", "whoami"], (await theirs.ListToolsAsync()).Select(t => t.Name).Order());
            Assert.Equal("the site's whoami", Text(await theirs.CallToolAsync("whoami", new Dictionary<string, object?>())));
        }
        await using var ours = await ToolTests.Connect(site);
        var tools = await ours.ListToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name == "site_only");
        Assert.StartsWith("Who the assistant acts as", Assert.Single(tools, t => t.Name == "whoami").Description);
        Assert.Contains("\"name\":\"editor\"", Text(await ours.CallToolAsync("whoami", new Dictionary<string, object?>())));
    }

    [Fact]
    public async Task A_built_in_tool_answers_arguments_that_dont_bind_with_a_usage_error()
    {
        await using var site = await TestSite.StartAsync();
        await using var client = await ToolTests.Connect(site);

        var missing = Error(await Call(client, "get_content", new { lang = "en" }));
        Assert.Equal((AgentErrorCodes.Usage, "Check the tool's input schema."), (missing.Code, missing.Hint));
        Assert.Contains("'reference'", missing.Message);
        var wrongType = Error(await Call(client, "move_content", new { reference = "5", destination = "6", dryRun = "yes" }));
        Assert.Equal(AgentErrorCodes.Usage, wrongType.Code);
        Assert.Contains("'dryRun'", wrongType.Message);
        Assert.Equal(2, site.Audit.Audit.Count(m => m.Contains(" failed: user editor", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_editor_works_only_in_the_call_it_was_made_for()
    {
        await using var site = await TestSite.StartAsync(module: m => m.WithTools<KeepingTools>());
        await using var client = await ToolTests.Connect(site);

        // In its own call: the load runs (and finds nothing to load).
        Assert.Equal(AgentErrorCodes.NotFound, Error(await Call(client, "custom_keep", new { keep = true })).Code);
        // The same instance in the next call: refused before it touches the CMS, as a failure of the site.
        Assert.Equal(AgentErrorCodes.Internal, Error(await Call(client, "custom_keep", new { keep = false })).Code);
        Assert.Contains(site.Audit.Entries, e => e.Message == "The site's MCP tool custom_keep failed.");
    }

    [Fact]
    public void Read_tools_are_checked_where_they_are_registered()
    {
        static InvalidOperationException Refused(Action<IOptiCliMcpBuilder> register)
        {
            var module = new ServiceCollection().AddOptiCliMcp();
            return Assert.Throws<InvalidOperationException>(() => register(module));
        }

        Assert.Contains("NoToolMethods", Refused(m => m.WithTools<NoToolMethods>()).Message);
        Assert.Contains("has no method with [McpServerTool]", Refused(m => m.WithTools<NoToolMethods>()).Message);

        var unnamed = Refused(m => m.WithTools<UnnamedTool>()).Message;
        Assert.Contains($"WithTools<{typeof(UnnamedTool).FullName}>(), method Run", unnamed);
        Assert.Contains("has no Name", unnamed);

        var writing = Refused(m => m.WithTools<WritingTool>()).Message;
        Assert.Contains("method Run", writing);
        Assert.Contains("'custom_write' isn't ReadOnly = true", writing);
        Assert.Contains("WithWriteTool", writing);
        // Private tool methods too: the SDK makes tools of them.
        Assert.Contains("method Hidden", Refused(m => m.WithTools<PrivateWritingTool>()).Message);

        foreach (var name in new[] { "get_content", "whoami", "delete_content" })
        {
            var builtIn = Refused(BuiltInNamed(name)).Message;
            Assert.Contains($"'{name}' is the name of one of the opticli module's own tools", builtIn);
        }
        Assert.Contains("isn't a valid MCP tool name", Refused(m => m.WithTools<InvalidNameTool>()).Message);
        Assert.Contains("two tools named 'custom_twice' (methods One and Two)", Refused(m => m.WithTools<TwiceInOneClass>()).Message);

        var clash = Refused(m => m.WithTools<CustomReadTools>().WithTools<SameNameAsCustom>()).Message;
        Assert.Contains($"'custom_whoami' is already the name of the site's tool {typeof(CustomReadTools).FullName}.WhoAmI", clash);
        Assert.Contains(typeof(SameNameAsCustom).FullName!, clash);

        Assert.Contains("is registered twice", Refused(m => m.WithTools<CustomReadTools>().WithTools<CustomReadTools>()).Message);
        Assert.Contains("abstract", Refused(m => m.WithTools<AbstractTools>()).Message);

        // A refused class leaves nothing behind: the next one may take its names.
        var module = new ServiceCollection().AddOptiCliMcp();
        Assert.Throws<InvalidOperationException>(() => module.WithTools<TwiceInOneClass>());
        module.WithTools<CustomReadTools>();
    }

    /// <summary>A tool class whose one tool is named <paramref name="name"/>, as <c>WithTools&lt;T&gt;()</c> takes it.</summary>
    private static Action<IOptiCliMcpBuilder> BuiltInNamed(string name) => name switch
    {
        "get_content" => m => m.WithTools<NamedGetContent>(),
        "whoami" => m => m.WithTools<NamedWhoAmI>(),
        _ => m => m.WithTools<NamedDeleteContent>(),
    };

    [Fact]
    public async Task A_tool_the_sdk_can_not_make_fails_the_start_naming_its_method()
    {
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => TestSite.StartAsync(module: m => m.WithTools<OpenGenericTool>()));

        Assert.Contains($"WithTools<{typeof(OpenGenericTool).FullName}>(), method Run", failed.Message);
        Assert.Contains("can't make a tool of it", failed.Message);
    }

    [Fact]
    public void The_builder_is_not_the_sdks_so_the_sdks_own_registrations_cant_apply_to_it()
    {
        Assert.False(typeof(IMcpServerBuilder).IsAssignableFrom(typeof(IOptiCliMcpBuilder)));
        var services = new ServiceCollection();
        Assert.Same(services, services.AddOptiCliMcp().Services);
    }
}

/// <summary>A site's read tools, as the docs show them: no [McpServerToolType], a name and ReadOnly on every tool.</summary>
public sealed class CustomReadTools(IOptiCliEditor editor)
{
    [McpServerTool(Name = "custom_whoami", Title = "Custom: who am I", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Who the site's tool runs for.")]
    public string WhoAmI([Description("Echoed back.")] string text) =>
        JsonSerializer.Serialize(new { editor = editor.Name, scopes = editor.Scopes, write = editor.HasScope(Scopes.Write), text });

    [McpServerTool(Name = "custom_fail", ReadOnly = true)]
    [Description("Fails as asked.")]
    public static string Fail(string kind, int count = 0) => kind switch
    {
        "tool" => throw new OptiCliToolException(AgentErrorCodes.NotFound, "No product called that.", "Search with other words."),
        "invalid" => throw new InvalidOperationException("Connection string Server=secret"),
        "mcp" => throw new McpException("An McpException's message."),
        // As the SDK's binding errors look, but the tool's own: its arguments bound.
        "argument" => throw new ArgumentException("The site's own argument check.", "arguments"),
        "json" => throw new JsonException("The site's own JSON."),
        _ => "ok",
    };
}

public sealed class PlainCustomTools(IHttpContextAccessor http)
{
    [McpServerTool(Name = "plain_custom", ReadOnly = true), Description("A custom tool without [McpServerToolType].")]
    public string Run() => "ran as " + (http.HttpContext?.User.Identity is { IsAuthenticated: true, Name: { } name } ? name : "anonymous");
}

/// <summary>With [McpServerToolType], against the docs: the SDK's WithToolsFromAssembly() picks it up.</summary>
[McpServerToolType]
public sealed class AttributedCustomTools(IHttpContextAccessor http)
{
    [McpServerTool(Name = "attributed_custom", ReadOnly = true), Description("A custom tool with [McpServerToolType].")]
    public string Run() => "ran as " + (http.HttpContext?.User.Identity is { IsAuthenticated: true, Name: { } name } ? name : "anonymous");
}

/// <summary>The site's own MCP server's tool.</summary>
[McpServerToolType]
public sealed class SiteServerTools
{
    [McpServerTool(Name = "site_editor"), Description("Asks for the module's editor service at another endpoint.")]
    public static string Editor(IServiceProvider services) => services.GetRequiredService<IOptiCliEditor>().Name;
}

/// <summary>The site's own MCP server with a tool of a built-in tool's name.</summary>
public sealed class ClashingSiteTools
{
    [McpServerTool(Name = "whoami"), Description("The site's own whoami.")]
    public static string WhoAmI() => "the site's whoami";

    [McpServerTool(Name = "site_only"), Description("The site's own tool.")]
    public static string SiteOnly() => "site";
}

public sealed class NoToolMethods
{
    public static string Run() => "";
}

public sealed class UnnamedTool
{
    [McpServerTool(ReadOnly = true)]
    public static string Run() => "";
}

public sealed class WritingTool
{
    [McpServerTool(Name = "custom_write")]
    public static string Run() => "";
}

public sealed class PrivateWritingTool
{
    [McpServerTool(Name = "custom_hidden", ReadOnly = false)]
    private static string Hidden() => "";
}

public sealed class NamedGetContent
{
    [McpServerTool(Name = "get_content", ReadOnly = true)]
    public static string Run() => "";
}

public sealed class NamedWhoAmI
{
    [McpServerTool(Name = "whoami", ReadOnly = true)]
    public static string Run() => "";
}

public sealed class NamedDeleteContent
{
    [McpServerTool(Name = "delete_content", ReadOnly = true)]
    public static string Run() => "";
}

public sealed class InvalidNameTool
{
    [McpServerTool(Name = "search products", ReadOnly = true)]
    public static string Run() => "";
}

public sealed class TwiceInOneClass
{
    [McpServerTool(Name = "custom_twice", ReadOnly = true)]
    public static string One() => "";

    [McpServerTool(Name = "custom_twice", ReadOnly = true)]
    public static string Two() => "";
}

public sealed class SameNameAsCustom
{
    [McpServerTool(Name = "custom_whoami", ReadOnly = true)]
    public static string Run() => "";
}

public abstract class AbstractTools
{
    [McpServerTool(Name = "custom_abstract", ReadOnly = true)]
    public string Run() => "";
}

public sealed class OpenGenericTool
{
    [McpServerTool(Name = "custom_generic", ReadOnly = true)]
    public static string Run<T>() => typeof(T).Name;
}

/// <summary>Against the docs: keeps the editor of one call for the next.</summary>
public sealed class KeepingTools(IOptiCliEditor editor)
{
    private static IOptiCliEditor? _kept;

    [McpServerTool(Name = "custom_keep", ReadOnly = true), Description("Loads nothing with this call's editor, or the one kept from an earlier call.")]
    public string Run(bool keep)
    {
        if (keep)
        {
            _kept = editor;
        }
        return (_kept ?? editor).Load<EPiServer.Core.IContent>(EPiServer.Core.ContentReference.EmptyReference).Name;
    }
}
