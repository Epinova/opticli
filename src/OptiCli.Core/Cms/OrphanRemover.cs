using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Cms;

/// <summary>
/// <c>types remove</c>, <c>types remove-property</c> and <c>types prune</c>: content types and properties that removed code
/// left in the database, removed through the running site (<see cref="AgentRoutes.TypesRemove"/>), which decides what is
/// an orphan. Only the shared-database refusal and the arguments are checked here first; there is no database fallback,
/// since only the site can say which classes it can load.
/// </summary>
public static class OrphanRemover
{
    /// <summary>What the commands print: the site's answer, its warnings moved to <c>meta.warnings</c>.</summary>
    public sealed record Output(
        IReadOnlyList<RemovedContentType> Types,
        IReadOnlyList<RemovedProperty> Properties,
        IReadOnlyList<KeptOrphan>? Kept,
        bool Removed,
        bool? DryRun);

    /// <exception cref="RefusedException">A shared (remote) database: checked before the site is asked.</exception>
    public static void RequireLocal(bool sharedDatabase)
    {
        if (sharedDatabase)
        {
            throw new RefusedException(OrphanRemoval.SharedRefusal, OrphanRemoval.SharedHint);
        }
    }

    /// <summary><c>types remove-property &lt;type&gt; &lt;prop&gt;...</c> as references; a property named twice counts once.</summary>
    /// <exception cref="UsageException">No type, or no property.</exception>
    public static IReadOnlyList<OrphanPropertyRef> Properties(string type, IReadOnlyList<string> properties)
    {
        var name = type.Trim();
        if (name.Length == 0)
        {
            throw new UsageException("The content type is empty.", "opticli types remove-property <type> <property>...");
        }
        var names = properties.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
        {
            throw new UsageException("Name at least one property to remove.", $"`opticli type {name}` lists its properties; existsOnModel: false marks those not in the code.");
        }
        return names.Select(p => new OrphanPropertyRef(name, p)).ToList();
    }

    /// <summary>Asks the site; the answer's warnings come back apart from what was removed.</summary>
    /// <exception cref="NotFoundException">The site's agent is older than this opticli (no such route).</exception>
    public static async Task<(Output Output, IReadOnlyList<string> Warnings)> RunAsync(AgentClient agent, OrphanRemovalRequest request, CancellationToken cancellationToken)
    {
        OrphanRemovalResult result;
        try
        {
            result = await agent.SendAsync<OrphanRemovalResult>(HttpMethod.Post, AgentRoutes.TypesRemove, request, cancellationToken);
        }
        catch (OptiCliException ex) when (IsOlderAgent(ex))
        {
            throw new NotFoundException("The site's agent is older than this opticli and can't remove content types or properties.", AgentErrors.OutOfDateHint);
        }
        return (new Output(result.Types, result.Properties, request.Prune ? result.Kept : null, result.Removed, result.DryRun ? true : null), result.Warnings ?? []);
    }

    /// <summary>
    /// An agent from before this route answers with no route at all, or (0.13) takes <c>types/remove</c> for reading the
    /// type named "remove", which it only does with GET.
    /// </summary>
    internal static bool IsOlderAgent(OptiCliException ex) =>
        (ex is NotFoundException && ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        || (ex is UsageException && ex.Message.Contains("/types/remove does not accept POST", StringComparison.Ordinal));
}
