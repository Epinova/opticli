namespace OptiCli.Mcp;

/// <summary>
/// A tool of the site's that changes something outside the CMS's content (<see cref="OptiCliMcpBuilderExtensions.WithActionTool{T}"/>):
/// offers in an external system, a search index, a translation service. The module runs the same flow as for a write
/// tool: the dry run the assistant shows the user (<see cref="PreviewAsync"/>), a token that runs only what that dry run
/// showed, the scopes, the publish and delete gates and the audit. The change itself is the site's code
/// (<see cref="RunAsync"/>).
/// </summary>
/// <typeparam name="TArgs">
/// The tool's arguments, as a record or class: its properties are the tool's input schema, as the assistant sees it
/// (<c>[Description]</c> on each), camelCase. A required constructor parameter is a required argument; one with a default
/// is optional. An argument the type doesn't have, a missing required one, or <c>null</c> for one that isn't nullable
/// is refused before the tool runs.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>What the module does and what the tool does.</b> A write tool's changes are made by the module, with the CMS's
/// access rights and validation. An action tool's are made by its own code: the module can't see what
/// <see cref="RunAsync"/> does. So the tool answers for its preview being honest (it lists everything the run changes)
/// and for checking that the editor may make the change: check the CMS's access rights on the content the thing belongs
/// to with <see cref="IOptiCliEditor.HasAccess"/>, and throw <see cref="OptiCliToolException"/> with <c>refused</c>
/// where they may not.
/// </para>
/// <para>
/// <b>The token.</b> The real run previews again and runs only when the preview is the one the dry run showed: the same
/// targets, effects, changes and versions. Build the preview from the current state, so a change made meanwhile (by
/// someone else, or by an earlier run) gives another preview: give each target a <see cref="ActionTarget.Version"/>
/// where the system has one, and the <c>before</c> of every <see cref="ActionTarget.Change"/>. And keep it the same for
/// the same state: no timestamps of now, new GUIDs or random order. A token runs once: the module keeps a record of each
/// run, and the same token again answers with that run's result instead of running again.
/// </para>
/// <para>
/// The class is made once per call from the request's services, as a read tool's is: the real run's
/// <see cref="PreviewAsync"/> and <see cref="RunAsync"/> are on the same instance. It is disposed after the call if it
/// is <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>.
/// </para>
/// </remarks>
public interface IOptiCliActionTool<TArgs>
{
    /// <summary>
    /// What a call with <paramref name="args"/> would do now, one target per thing it changes, in the order
    /// <see cref="RunAsync"/> changes them; nothing is changed here. Problems (<see cref="ActionTarget.Problem"/>: a
    /// validator's errors, a name that is taken) make the dry run invalid, so no token is given. Return an empty preview
    /// when there is nothing to do.
    /// </summary>
    /// <exception cref="OptiCliToolException">Arguments the assistant should change, something that isn't there, a change the editor may not make: the assistant gets it as the tool's error.</exception>
    Task<ActionPreview> PreviewAsync(TArgs args, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the changes <paramref name="preview"/> shows, in its order, and reports each target on
    /// <paramref name="run"/> as soon as it is done (<see cref="ActionRun.Done"/>). Called only after the module has
    /// previewed again on this instance and found the preview the token covers; that preview is
    /// <paramref name="preview"/>.
    /// </summary>
    /// <remarks>
    /// To stop at a target that fails, throw: <see cref="OptiCliToolException"/> with what the assistant should tell the
    /// user, or let the system's own exception through (an <c>internal</c> error, logged). The first target not reported
    /// done is the one that failed; those before it stay done, those after it didn't run. Nothing is undone. A call
    /// cancelled during the run leaves the target it was on <c>unknown</c>: it may or may not have changed. Not called
    /// for an empty preview.
    /// </remarks>
    Task RunAsync(TArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken);
}
