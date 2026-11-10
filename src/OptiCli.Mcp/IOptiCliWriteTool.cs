namespace OptiCli.Mcp;

/// <summary>
/// A tool of the site's that changes content (<see cref="OptiCliMcpBuilderExtensions.WithWriteTool{T}"/>): it says what
/// to change, as a <see cref="ContentPlan"/>, and the module makes the changes, as the editor, through its own content
/// operations. The module does the dry run, which the assistant shows the user first, and then the real run, with the
/// same checks as its own tools: access rights, the edit UI's rules, <c>baseVersion</c> conflicts, validation, the delete
/// gate. Every change is a draft; nothing in a plan publishes.
/// </summary>
/// <typeparam name="TArgs">
/// The tool's arguments, as a record or class: its properties are the tool's input schema, as the assistant sees it
/// (<c>[Description]</c> on each), camelCase. A required constructor parameter is a required argument; one with a default
/// is optional. An argument the type doesn't have, a missing required one, or <c>null</c> for one that isn't nullable
/// is refused before the tool runs.
/// </typeparam>
/// <remarks>
/// <para>
/// The module calls <see cref="PlanAsync"/> twice: for the dry run, and again for the real run, on a new instance (the
/// server keeps nothing between calls). It compares the two plans and refuses the real run when they differ: the content
/// changed meanwhile, and the user hasn't seen the new changes. So a plan must be the same for the same content and
/// arguments: no timestamps, new GUIDs or random order, and items in an order of the content's own (sort them).
/// </para>
/// <para>
/// Plan from what the editor may read: load through <see cref="IOptiCliEditor"/> (with <c>latest: true</c>, the version a
/// change is made from), or filter with its <c>Readable</c>. The module checks every step as the editor anyway. Check the
/// arguments as for any tool, and throw <see cref="OptiCliToolException"/> for one the assistant should change.
/// </para>
/// <para>
/// The class is made for every call from the request's services, as a read tool's is, and disposed after
/// <see cref="PlanAsync"/> if it is <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>.
/// </para>
/// </remarks>
public interface IOptiCliWriteTool<TArgs>
{
    /// <summary>
    /// The changes to make for <paramref name="args"/>, in order: nothing is changed here, only planned. Return an empty
    /// plan when there is nothing to change.
    /// </summary>
    /// <exception cref="OptiCliToolException">Arguments the assistant should change, content that isn't there: the assistant gets it as the tool's error.</exception>
    Task<ContentPlan> PlanAsync(TArgs args, CancellationToken cancellationToken);
}
