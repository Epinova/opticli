namespace OptiCli.Mcp;

/// <summary>
/// Makes a class a write tool of the site's (<see cref="OptiCliMcpBuilderExtensions.WithWriteTool{T}"/>): its MCP tool
/// name, title and the kinds of steps its plans may have. Put <c>[Description]</c> on the class too: what the tool
/// changes, for the assistant.
/// </summary>
/// <example>
/// <code>
/// [OptiCliWriteTool("retag_articles", Title = "Retag articles", Kinds = ContentPlanKinds.Update)]
/// [Description("Replaces one tag with another on every article page below a page.")]
/// public sealed class RetagArticlesTool(IOptiCliEditor editor, IContentLoader loader) : IOptiCliWriteTool&lt;RetagArgs&gt;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OptiCliWriteToolAttribute(string name) : Attribute
{
    /// <summary>
    /// The tool's MCP name, e.g. <c>retag_articles</c>: 1 to 128 of the letters A-Z and a-z, digits, <c>_</c>, <c>-</c>
    /// and <c>.</c>; none of the module's tools' names, none of the site's other tools'.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>A title for people, e.g. <c>Retag articles</c>; the client may show it instead of the name.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// The kinds of steps the tool's plans may have; at least one. A plan with a step of another kind fails as a fault of
    /// the site. A tool that may move to the recycle bin (<see cref="ContentPlanKinds.Trash"/>) is offered only where the
    /// site allows deleting (<see cref="OptiCliMcpOptions.AllowDelete"/>).
    /// </summary>
    public ContentPlanKinds Kinds { get; set; }
}

/// <summary>The kinds of step a <see cref="ContentPlan"/> has, as a write tool declares them (<see cref="OptiCliWriteToolAttribute.Kinds"/>).</summary>
[Flags]
public enum ContentPlanKinds
{
    /// <summary>None: not a valid declaration.</summary>
    None = 0,

    /// <summary>A new draft of existing content (<see cref="ContentPlan.Update"/>).</summary>
    Update = 1,

    /// <summary>New content, saved as a draft (<see cref="ContentPlan.Create(EPiServer.Core.IContent, string, string, Action{ContentChanges}?, string?)"/>).</summary>
    Create = 2,

    /// <summary>Content moved below another parent (<see cref="ContentPlan.Move(EPiServer.Core.IContent, EPiServer.Core.IContent)"/>).</summary>
    Move = 4,

    /// <summary>Content moved to the recycle bin (<see cref="ContentPlan.Trash"/>), where an editor can restore it.</summary>
    Trash = 8,
}
