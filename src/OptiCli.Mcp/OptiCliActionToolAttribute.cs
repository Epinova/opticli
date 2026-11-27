namespace OptiCli.Mcp;

/// <summary>
/// Makes a class an action tool of the site's (<see cref="OptiCliMcpBuilderExtensions.WithActionTool{T}"/>): its MCP
/// tool name, title and the most its calls may do. Put <c>[Description]</c> on the class too: what the tool changes, for
/// the assistant.
/// </summary>
/// <example>
/// <code>
/// [OptiCliActionTool("publish_event", Title = "Publish event", Effects = ActionEffects.Live)]
/// [Description("Publishes an event in the site's event calendar, so visitors see it.")]
/// public sealed class PublishEventTool(IOptiCliEditor editor, IEventCalendar calendar) : IOptiCliActionTool&lt;PublishEventArgs&gt;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OptiCliActionToolAttribute(string name) : Attribute
{
    /// <summary>
    /// The tool's MCP name, e.g. <c>publish_event</c>: 1 to 128 of the letters A-Z and a-z, digits, <c>_</c>, <c>-</c>
    /// and <c>.</c>; none of the module's tools' names, none of the site's other tools'.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>A title for people, e.g. <c>Publish event</c>; the client may show it instead of the name.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// The most a call may do; at least one. A preview with a target of another effect fails as a fault of the site. It
    /// decides where the tool is offered and what it needs: <see cref="ActionEffects.Delete"/> only where the site allows
    /// deleting (<see cref="OptiCliMcpOptions.AllowDelete"/>); a tool whose only effect is <see cref="ActionEffects.Live"/>
    /// is refused up front where the site doesn't let assistants publish.
    /// </summary>
    public ActionEffects Effects { get; set; }
}

/// <summary>
/// What an action tool's change does to the site: what it declares (<see cref="OptiCliActionToolAttribute.Effects"/>),
/// and each target of a preview (<see cref="ActionPreview.Add"/>). Every action needs the <c>content:write</c> scope.
/// </summary>
[Flags]
public enum ActionEffects
{
    /// <summary>None: not a valid declaration, nor a target's effect.</summary>
    None = 0,

    /// <summary>
    /// Changes something visitors don't see: a draft, a setting the site applies later, a search index rebuilt from what
    /// is published.
    /// </summary>
    Change = 1,

    /// <summary>
    /// Changes what visitors see: publishes, unpublishes, updates something live. Also needs the site to let assistants
    /// publish (<see cref="OptiCliMcpOptions.AllowPublish"/>) and the <c>content:publish</c> scope, as
    /// <c>publish_content</c> does.
    /// </summary>
    Live = 2,

    /// <summary>
    /// Deletes something. The tool is offered only where the site allows deleting
    /// (<see cref="OptiCliMcpOptions.AllowDelete"/>), as <c>delete_content</c> is.
    /// </summary>
    Delete = 4,
}
