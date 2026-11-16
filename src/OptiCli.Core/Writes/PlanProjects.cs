using System.Globalization;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>A project an <c>apply</c> plan saves into (<see cref="PlanRun.Projects"/>).</summary>
/// <param name="Id">Null for a dry run's project that doesn't exist yet.</param>
/// <param name="Status">
/// <c>existing</c>; <c>created</c> by this run; <c>toCreate</c> for a dry run (the real run makes it before the first
/// operation saves anything).
/// </param>
/// <param name="MarkedReady">
/// The versions the run marked Ready to publish after its last operation, for steps with <c>ready</c> whose content a later
/// operation changed again (or sent for review, where an approval sequence applies and the step asked for that).
/// </param>
/// <param name="Undo">For a project the run made: how to delete it again (the drafts in it stay).</param>
public sealed record PlanProject(int? Id, string Name, string Status, IReadOnlyList<string>? MarkedReady = null, string? Undo = null);

/// <summary>
/// The projects a plan names (the plan's <c>project</c> and the steps' own): resolved against the database before the
/// plan is checked, made when it runs (before its first step) if no unpublished project has the name, and the steps
/// marked <c>ready</c> made ready in the end where a later step changed the same content again.
/// </summary>
/// <remarks>
/// A project is named as the site names one (<see cref="ProjectReader.Find"/>): an id, else the exact name, of which a
/// project that isn't published wins. A name only published projects have gets a new project, as there is nothing to save
/// into; a number that is no project's id (nor name) is refused rather than made into a project named after it.
/// </remarks>
internal sealed class PlanProjects
{
    public const string Existing = "existing";
    public const string Created = "created";
    public const string ToCreate = "toCreate";

    private sealed class Entry(string name)
    {
        public string Name { get; } = name;

        /// <summary>The existing project's own name, where the plan names it by id.</summary>
        public string? ProjectName { get; init; }

        /// <summary>The project's id, once known (an existing project, or one the run made).</summary>
        public int? Id { get; set; }

        public bool Make { get; init; }

        public bool Made { get; set; }

        public List<string> MarkedReady { get; } = [];
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="UsageException">A name more than one unpublished project has.</exception>
    /// <exception cref="NotFoundException">A number that is no project's id or name.</exception>
    public static async Task<PlanProjects> ResolveAsync(ContentSession session, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
    {
        var resolved = new PlanProjects();
        var names = steps.Select(s => s.Operation.Project?.Trim()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
        {
            return resolved;
        }
        var projects = await ProjectReader.ListAsync(session.Db, cancellationToken);
        foreach (var name in names)
        {
            resolved._entries[name] = Resolve(projects, name);
        }
        return resolved;
    }

    /// <summary>The project a plan's name stands for: its id, or null when the run makes it (<see cref="Resolve(IReadOnlyList{ProjectInfo}, string)"/>).</summary>
    internal static int? IdFor(IReadOnlyList<ProjectInfo> projects, string name) => Resolve(projects, name).Id;

    private static Entry Resolve(IReadOnlyList<ProjectInfo> projects, string name)
    {
        var found = ProjectReader.Find(projects, name);
        var isId = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
        if (found is not null && (found.Unpublished || (isId && found.Id == id)))
        {
            // A published project named by its id is the site's to refuse (with what to do instead).
            return new Entry(name) { Id = found.Id, ProjectName = found.Name };
        }
        if (isId)
        {
            throw new NotFoundException(
                $"The plan names project {name}, and no project has that id. A number is taken as a project's id, so the plan doesn't make a project named after it.",
                "Give the project's id (`opticli projects` lists them), or a name for the plan to make it.");
        }
        return new Entry(name) { Make = true };
    }

    private Entry? For(WriteOperation operation) => operation.Project?.Trim() is { } name && _entries.TryGetValue(name, out var entry) ? entry : null;

    /// <summary>The operation as the plan's check dry-runs it: in its project by id, or without one the run has yet to make.</summary>
    public WriteOperation ForCheck(WriteOperation operation) => For(operation) switch
    {
        null => operation,
        { Id: { } id } => operation with { Project = id.ToString(CultureInfo.InvariantCulture) },
        _ => operation with { Project = null, Ready = false, ProjectFromPlan = false },
    };

    /// <summary>The operation as the run saves it: in its project by id (made by then).</summary>
    public WriteOperation ForRun(WriteOperation operation) => For(operation) is { Id: { } id }
        ? operation with { Project = id.ToString(CultureInfo.InvariantCulture) }
        : operation;

    /// <summary>The operation's project doesn't exist yet: the run makes it, so the plan's check runs without it.</summary>
    public bool ToMake(WriteOperation operation) => For(operation) is { Id: null };

    /// <summary>A step's check, with a note when its project doesn't exist yet and so wasn't part of it.</summary>
    /// <param name="operation">The step as the plan gives it.</param>
    public PlanStepResult Note(WriteOperation operation, PlanStepResult result)
    {
        if (For(operation) is not { Id: null } entry || result.Status == PlanStepStatus.Invalid)
        {
            return result;
        }
        var note = $"Project '{entry.Name}' doesn't exist yet: the run makes it before the first operation saves anything, and saves this operation's draft in it{(operation.Ready ? ", marked Ready to publish" : "")}. This check ran without the project, so content that is in another project already is refused only when the plan runs (the project made by then stays, with the operations saved before it).";
        return result with { Warnings = [.. result.Warnings ?? [], note] };
    }

    /// <summary>Makes the projects that don't exist yet.</summary>
    public async Task CreateAsync(ProjectWrites writes, CancellationToken cancellationToken)
    {
        foreach (var entry in _entries.Values.Where(e => e.Make && !e.Made))
        {
            var result = await writes.CreateAsync(entry.Name, dryRun: false, cancellationToken);
            entry.Id = result.Project.Id;
            entry.Made = true;
        }
    }

    /// <summary>
    /// The steps as they run, where a step with <c>ready</c> is followed by another that changes the same content (the
    /// same ref, or the <c>$id</c> it creates): the earlier one saves without <c>ready</c>, so the later change goes into
    /// its draft instead of a new version, and <see cref="ReadyAsync"/> marks the content's last version ready.
    /// </summary>
    public static List<PlanStep> DeferReady(List<PlanStep> steps) => steps.Select((step, i) =>
    {
        if (step.Operation is not { Ready: true, Project: not null } op || op is TranslateOperation { WithBlocks: true } || Wrote(op) is not { } written)
        {
            return step;
        }
        var changedLater = steps.Skip(i + 1).Any(later => later.Operation.Project is not null && Changes(later.Operation) is { } target
            && string.Equals(target.Trim(), written.Trim(), StringComparison.Ordinal));
        // Its review request goes with the ready (ReadyAsync sends it): saved now, it would start the review (or, where no
        // sequence applies, be refused) before the later step's change.
        return changedLater ? step with { Operation = op with { Ready = false, RequestApproval = false } } : step;
    }).ToList();

    /// <summary>
    /// For a plan that stopped at <paramref name="failed"/>: the operations before it whose <c>ready</c> was deferred to the
    /// end of the run (<see cref="DeferReady"/>), and so are drafts in their project, not ready; null when there are none.
    /// </summary>
    public static string? NotMarked(IReadOnlyList<PlanStep> running, IReadOnlyList<PlanStep> planned, int failed)
    {
        var deferred = running.Where(s => s.Index < failed && !s.Operation.Ready && planned.FirstOrDefault(p => p.Index == s.Index)?.Operation is { Ready: true, Project: not null })
            .Select(s => s.Index).ToList();
        return deferred.Count == 0
            ? null
            : $"Operation(s) {string.Join(", ", deferred)} asked for ready, which the run gives once its last operation is saved, so they are drafts in their project, not ready: run the plan again once the problem is fixed, or mark them with `opticli projects ready <project> <ref>...`.";
    }

    /// <summary>The content a step saves, as later steps name it: its ref, or <c>$id</c> for content it creates.</summary>
    private static string? Wrote(WriteOperation operation) => operation switch
    {
        CreateOperation or BlockCreateOperation or UploadOperation { Replace: null } when operation.Id is { } id => "$" + id,
        _ => Changes(operation),
    };

    /// <summary>The existing content a step saves a new version of.</summary>
    private static string? Changes(WriteOperation operation) => operation switch
    {
        SetOperation set => set.Ref,
        AreaEdit area => area.Ref,
        CompositionEdit composition => composition.Ref,
        TranslateOperation { Remove: false } translate => translate.Ref,
        UploadOperation { Replace: { } replace } => replace,
        _ => null,
    };

    /// <summary>
    /// After the last step: for content a step asked to have ready (<c>ready</c>) whose last version in the project isn't
    /// (a later step saved another, or the step's ready was deferred), that version marked ready; where the step also asked
    /// for <c>requestApproval</c>, sent for review where an approval sequence applies.
    /// </summary>
    /// <param name="planned">The steps as the plan gives them (their <c>ready</c> as asked).</param>
    /// <param name="results">What each step saved.</param>
    public async Task ReadyAsync(ProjectWrites writes, IReadOnlyList<PlanStep> planned, IReadOnlyList<PlanStepResult> results, CancellationToken cancellationToken)
    {
        foreach (var (project, review, versions) in Unready(planned, results))
        {
            var entry = _entries.Values.FirstOrDefault(e => e.Id == project);
            var result = await writes.ReadyAsync(project.ToString(CultureInfo.InvariantCulture), versions, null, all: false, requestApproval: review, comment: null, includeDraft: false, dryRun: false, cancellationToken);
            entry?.MarkedReady.AddRange((result.Items ?? []).Where(i => i.Outcome is ProjectItemOutcomes.Ready or ProjectItemOutcomes.ApprovalRequested).Select(i => i.Content.Ref));
        }
    }

    /// <summary>
    /// What <see cref="ReadyAsync"/> marks: per project and whether to request approval, the last version of each content and
    /// language that a step asked to have ready, where that version is a draft that isn't.
    /// </summary>
    internal static IEnumerable<(int Project, bool Review, IReadOnlyList<string> Versions)> Unready(IReadOnlyList<PlanStep> planned, IReadOnlyList<PlanStepResult> results)
    {
        var asked = planned.Where(s => s.Operation is { Ready: true, Project: not null }).ToDictionary(s => s.Index, s => s.Operation.RequestApproval);
        if (asked.Count == 0)
        {
            return [];
        }
        var marks = new List<(int Project, bool Review, string Version)>();
        var saves = results
            .Where(r => r.Result is WriteOutput { Project: not null, Ref: not null, Version: not null })
            .GroupBy(r =>
            {
                var output = (WriteOutput)r.Result!;
                return (output.Project!.Id, output.Ref!, output.Language?.ToLowerInvariant());
            });
        foreach (var group in saves)
        {
            var wanted = group.Where(r => asked.ContainsKey(r.Index)).ToList();
            if (wanted.Count == 0)
            {
                continue;
            }
            var last = (WriteOutput)group.MaxBy(r => r.Index)!.Result!;
            if (last.Project!.Ready || last.ApprovalRequested == true || last.Status is not ("checkedOut" or "rejected"))
            {
                continue;
            }
            marks.Add((group.Key.Id, wanted.Any(r => asked[r.Index]), last.Version!));
        }
        return marks.GroupBy(m => (m.Project, m.Review)).Select(g => (g.Key.Project, g.Key.Review, (IReadOnlyList<string>)g.Select(m => m.Version).Distinct().ToList())).ToList();
    }

    /// <summary>The projects for <see cref="PlanRun.Projects"/>; null when the plan names none.</summary>
    public IReadOnlyList<PlanProject>? Report() => _entries.Count == 0
        ? null
        : _entries.Values.Select(e => new PlanProject(
            e.Id,
            e.ProjectName ?? e.Name,
            e.Made ? Created : e.Make ? ToCreate : Existing,
            e.MarkedReady.Count > 0 ? e.MarkedReady : null,
            e.Made ? $"opticli projects delete {e.Id} (the drafts saved into it stay as they are)" : null)).ToList();
}
