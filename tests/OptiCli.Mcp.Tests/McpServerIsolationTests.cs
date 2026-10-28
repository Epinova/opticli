using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
