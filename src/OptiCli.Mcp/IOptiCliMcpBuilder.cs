using Microsoft.Extensions.DependencyInjection;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp;

/// <summary>
/// What <see cref="OptiCliMcpExtensions.AddOptiCliMcp"/> returns, to add the site's own tools and instructions to the
/// module's MCP server: <c>services.AddOptiCliMcp().WithTools&lt;ProductSearchTools&gt;()</c>.
/// </summary>
/// <remarks>
/// Not the MCP SDK's <c>IMcpServerBuilder</c>, on purpose: the SDK's own <c>WithTools</c>, <c>WithPrompts</c> and the
/// like register into the one set of MCP options every server in the application shares, and the module's server serves
/// only what it registers itself (<see cref="OptiCliMcpBuilderExtensions"/>).
/// </remarks>
public interface IOptiCliMcpBuilder
{
    /// <summary>The site's services, to go on registering others after the module's.</summary>
    IServiceCollection Services { get; }
}

/// <summary>Adds the site's own tools and instructions to the opticli MCP module's server (<see cref="IOptiCliMcpBuilder"/>).</summary>
public static class OptiCliMcpBuilderExtensions
{
    /// <summary>
    /// Offers the site's own read tools to editors' assistants on the module's MCP endpoint, beside the module's: every
    /// method of <typeparamref name="T"/> with <c>[McpServerTool(Name = "...", ReadOnly = true)]</c>, written as for the
    /// MCP SDK (parameters, <c>[Description]</c>, async, <c>CancellationToken</c>, image results). The class is made for
    /// every call from the request's services; take <see cref="IOptiCliEditor"/> there to read as the editor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tools run only on the module's endpoint, for a signed-in editor, with the module's sign-in, audit and errors
    /// (<see cref="OptiCliToolException"/>): any other MCP server of the site that registers the same class doesn't get
    /// them. So don't put <c>[McpServerToolType]</c> on the class (the SDK's <c>WithToolsFromAssembly()</c> would pick it
    /// up for those), and don't register it with another server.
    /// </para>
    /// <para>
    /// Every tool needs a <c>Name</c> of its own (none of the module's tools', none of the site's others'), and
    /// <c>ReadOnly = true</c>: a tool that changes content is a write tool, which runs its changes through the module's
    /// dry run and drafts.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> has no tool method, one of them has no name, a name that is taken or isn't a valid MCP tool
    /// name, or isn't read-only; or <typeparamref name="T"/> was registered before. The message names the method.
    /// </exception>
    public static IOptiCliMcpBuilder WithTools<T>(this IOptiCliMcpBuilder builder) where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        CustomTools.Of(builder.Services).AddReadTools(typeof(T));
        return builder;
    }

    /// <summary>
    /// Offers the site's own write tool to editors' assistants on the module's MCP endpoint: <typeparamref name="T"/>
    /// implements <see cref="IOptiCliWriteTool{TArgs}"/>, which plans the changes, and has
    /// <see cref="OptiCliWriteToolAttribute"/> (its name, title and kinds of steps) and <c>[Description]</c>. The module
    /// makes its MCP tool: <c>TArgs</c>' properties as arguments, plus <c>dryRun</c> and <c>planToken</c>; it does the dry
    /// run and the real run itself, as the editor, saving drafts only (<see cref="ContentPlan"/>).
    /// </summary>
    /// <remarks>
    /// The tool needs the <c>content:write</c> scope, as the module's own changes do. One that may trash content
    /// (<see cref="ContentPlanKinds.Trash"/>) is offered only where the site allows deleting
    /// (<see cref="OptiCliMcpOptions.AllowDelete"/>). The class is made for every call from the request's services, as a
    /// read tool's is; take <see cref="IOptiCliEditor"/> there.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> has no <c>[OptiCliWriteTool]</c> or <c>[Description]</c>, no kinds, a name that is taken
    /// or isn't a valid MCP tool name, an arguments type the module can't use, or was registered before. The message names
    /// the class.
    /// </exception>
    public static IOptiCliMcpBuilder WithWriteTool<T>(this IOptiCliMcpBuilder builder) where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        CustomTools.Of(builder.Services).AddWriteTool(typeof(T));
        return builder;
    }

    /// <summary>
    /// Adds <paramref name="text"/> to the instructions the module's server gives assistants, after the module's own,
    /// under a heading that says they come from the site: how to work with the site's own tools and content, e.g.
    /// "Product pages live below Products; search them with search_products, not find_content." Each call adds a line.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty.</exception>
    public static IOptiCliMcpBuilder WithInstructions(this IOptiCliMcpBuilder builder, string text)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        CustomTools.Of(builder.Services).AddInstructions(text.Trim());
        return builder;
    }
}

/// <summary>The builder <see cref="OptiCliMcpExtensions.AddOptiCliMcp"/> returns.</summary>
internal sealed class OptiCliMcpBuilder(IServiceCollection services) : IOptiCliMcpBuilder
{
    public IServiceCollection Services { get; } = services;
}
