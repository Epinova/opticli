using System.Text.Json.Serialization;
using OptiCli.Core.Configuration;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Seals;

/// <summary>The production database a seal is for: as named, and as roles key it (<see cref="DatabaseKey"/>).</summary>
public sealed record SealTarget(string Server, string? Database, string KeyServer, string KeyDatabase)
{
    [JsonIgnore] public DatabaseKey Key => new(KeyServer, KeyDatabase);
}

/// <summary>The <c>apply</c> options the sealed run uses: the ones given when sealing, never others.</summary>
/// <param name="MovesFirst">A <c>sync push</c> plan: a create step after a <c>move</c> of the same content is dry-run where the content is now (<see cref="PlanRunner.MovesFirst"/>).</param>
public sealed record SealOptions(bool UpdateExisting, bool Publish, bool RequestApproval, bool MovesFirst = false)
{
    /// <summary>The options as <c>apply</c> takes them, for the summary.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Flags =>
        new[] { UpdateExisting ? "--update-existing" : null, Publish ? "--publish" : null, RequestApproval ? "--request-approval" : null }.OfType<string>().ToList();
}

/// <summary>What a plan does, counted: what the user is shown before they confirm.</summary>
/// <param name="Operations">Steps per op.</param>
/// <param name="Types">Content created or written by a create/block step, per content type.</param>
/// <param name="Publishes">Steps that publish (or schedule a publish, or request approval).</param>
/// <param name="Existing">Create/block steps whose GUID production already has: those update it (with --update-existing).</param>
/// <param name="New">Create/block steps whose GUID production doesn't have: new content.</param>
/// <param name="Touched">Content the plan writes that production has, whose versions the seal checks.</param>
public sealed record SealSummary(int Steps, IReadOnlyDictionary<string, int> Operations, IReadOnlyDictionary<string, int> Types, int Publishes, int Existing, int New, int Touched)
{
    public static SealSummary Of(WritePlan plan, SealOptions options, ProductionState production)
    {
        var there = production.Items.Where(i => i.Exists).Select(i => i.Guid).ToHashSet();
        var operations = plan.Steps.Select(s => s.Operation).ToList();
        var creating = operations.Where(o => o is CreateOperation or BlockCreateOperation).ToList();
        var touched = SealedPlan.Touched(plan);
        return new SealSummary(
            operations.Count,
            Count(operations.Select(o => o.Kind)),
            Count(operations.Select(o => o switch { CreateOperation create => string.IsNullOrEmpty(create.Type) ? $"blueprint {create.Blueprint}" : create.Type, BlockCreateOperation block => block.Type, _ => null }).OfType<string>()),
            operations.Count(o => o.Publishes || o.RequestApproval || (options.Publish && o.WithPublish().Publishes)),
            creating.Count(o => o.ContentGuid is { } guid && there.Contains(guid)),
            creating.Count(o => o.ContentGuid is not { } guid || !there.Contains(guid)),
            touched.Count(there.Contains));
    }

    private static IReadOnlyDictionary<string, int> Count(IEnumerable<string> names) =>
        names.GroupBy(n => n, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}

/// <summary>
/// A plan sealed for production: what the agent prepared and checked (<c>apply --dry-run --seal-for</c>, <c>sync push</c>),
/// for the user to run once in their own terminal (<c>apply --db &lt;name&gt; --seal &lt;id&gt;</c>). Kept in the user config
/// folder with a MAC (<see cref="SealStore"/>). The run compares what it can see now with it, and refuses on any difference.
/// </summary>
/// <param name="Origin"><c>apply</c> or <c>sync push</c>.</param>
/// <param name="Project">The site project's directory: the run must be in the same project.</param>
/// <param name="Plan">The plan file's full path.</param>
/// <param name="PlanHash">SHA-256 of the plan and the files it reads (<see cref="PlanSnapshot.Hash"/>).</param>
/// <param name="ValidatedOn">The local database the plan's dry run passed against.</param>
public sealed record Seal(
    string Id,
    DateTimeOffset Created,
    string Origin,
    string Project,
    SealTarget Target,
    string Plan,
    string PlanHash,
    int PlanFiles,
    SealOptions Options,
    BuildFingerprint Build,
    ProductionState Production,
    SealSummary Summary,
    string ValidatedOn,
    string OptiCliVersion)
{
    /// <summary>How long a seal can be run: a day. Not configurable, so the command an agent hands over can't stretch it.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public DateTimeOffset Expires => Created + MaxAge;
}
