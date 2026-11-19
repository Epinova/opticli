using System.Globalization;
using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// The blocks a plan's area adds make, where the site's edit UI makes a new block in a ContentArea a shared block in the
/// content's "For this page" folder (<c>status</c>'s <c>inlineBlocks</c> is <c>shared</c>; the agent decides it per write).
/// </summary>
/// <remarks>
/// <para>An area add with a <c>type</c> may have an <c>id</c>: later operations reach the block it makes as <c>"$id"</c>
/// (a set of its values, a publish, an item of another area), as they reach content a create makes. Where the site makes
/// inline blocks, that block has no ref: later operations name it by its position (<c>MainArea[2]</c>) instead.</para>
/// <para>Where it makes shared blocks, the reverse holds: once the add has run, the block is content of its own, and an
/// operation that names it by its position to change its values (<c>MainArea[2].Heading</c>, <c>area set</c>, an area
/// inside it) is refused. A plan's dry run reports such an operation (<see cref="OnPlannedBlock"/>).</para>
/// </remarks>
public static class PlanBlocks
{
    /// <summary>Whether <paramref name="op"/> is an area add that names the block it makes (<c>id</c>).</summary>
    public static bool Names(WriteOperation op) => op is AreaEdit { Action: AreaOps.Add, Type: not null, Id: not null };

    /// <summary>
    /// The content id of the shared block an area add with a <c>type</c> made (or, run again with <c>apply --update-existing</c>, found
    /// there already): the one <see cref="WriteOutput.SharedBlocks"/> entry for an item of the add's area itself (not one
    /// inside it). Null where it made an inline block, or the result can't tell which.
    /// </summary>
    public static int? MadeBlock(AreaEdit op, WriteOutput output)
    {
        if (op is not { Action: AreaOps.Add, Type: not null } || output.SharedBlocks is not { Count: > 0 } shared)
        {
            return null;
        }
        var made = shared.Where(s => s.Action is SharedBlockActions.Created or SharedBlockActions.Existing && s.Ref is not null && ItemOf(s.Path, op.Property)).ToList();
        return made is [var only] && int.TryParse(only.Ref!.Split('_')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>
    /// Where the site makes shared blocks: whether the dry run of an area add with a <c>type</c> (<paramref name="output"/>)
    /// left its area without a shared block for it, neither one it would make nor one of the folder it found (the add found
    /// an inline block like it there already, run again). False without a dry run to tell (a step checked only
    /// later).
    /// </summary>
    public static bool FoundInline(AreaEdit op, WriteOutput? output) =>
        op is { Action: AreaOps.Add, Type: not null } && output is { DryRun: true }
        && !(output.SharedBlocks ?? []).Any(s => s.Action is SharedBlockActions.Created or SharedBlockActions.Existing && ItemOf(s.Path, op.Property));

    /// <summary>
    /// The GUID a step's <paramref name="output"/> gives the content its plan id names, for <c>$id</c>s in rich text
    /// (<see cref="TextRefs"/>): the content's own; null for an area add, whose output is the content it adds to, not the
    /// block it made (whose GUID the output doesn't have).
    /// </summary>
    public static Guid? CreatedGuid(WriteOperation op, object? output) =>
        op is AreaEdit ? null : (output as WriteOutput)?.Guid;

    /// <summary>Whether <paramref name="path"/> (<c>MainArea[2]</c>) is an item of the area <paramref name="property"/> itself.</summary>
    private static bool ItemOf(string path, string property) =>
        path.StartsWith(property + "[", StringComparison.OrdinalIgnoreCase) && path.EndsWith(']')
        && path[(property.Length + 1)..^1].All(char.IsAsciiDigit);

    /// <summary>
    /// A ref field of <paramref name="operation"/> (as resolved for the run) that still names a plan id an area add gives
    /// its block: that add made no shared block, so there is nothing to reach.
    /// </summary>
    /// <returns>The refusal; null when every such ref was resolved.</returns>
    public static UsageException? Unresolved(WriteOperation operation, IReadOnlyList<PlanStep> steps, int index)
    {
        foreach (var reference in operation.Refs.OfType<string>().Concat(Values(operation)))
        {
            if (PlanSimulation.PlanId(reference) is { } id && steps.FirstOrDefault(s => s.Index < index && s.Operation.Id == id && Names(s.Operation)) is { } add)
            {
                return new UsageException(
                    $"'{reference}' is the block operation {add.Index} adds to {((AreaEdit)add.Operation).Property}, and that left no shared block to reach: the site makes new blocks in a ContentArea inline (its edit UI does; `opticli serve --status` shows inlineBlocks), or the add found an inline block like it there already, and an inline block has no ref.",
                    "Change the block by its position instead (\"MainArea[2]\": {...} in a set, or an area set), and leave out the add's \"id\". Operations before this one were saved; details lists them.");
            }
        }
        return null;
    }

    /// <summary>The whole string values of an operation's properties, which may name planned content (<c>"$teaser"</c>).</summary>
    private static IEnumerable<string> Values(WriteOperation operation)
    {
        var found = new List<string>();
        operation.MapRefs(value =>
        {
            found.Add(value);
            return value;
        });
        return found;
    }

    /// <summary>
    /// The ContentArea items <paramref name="op"/> names by their position to change their values: a set's
    /// <c>"MainArea[2]"</c>, an area set's index, the items an area path goes through (<c>MainArea[0].Area</c>). Moving or
    /// removing an item by its position works on any item, so those aren't listed.
    /// </summary>
    public static IReadOnlyList<string> Positions(WriteOperation op) => op switch
    {
        AreaEdit { Action: AreaOps.Set, Index: { } index } set => [.. Through(set.Property), $"{set.Property}[{index}]"],
        AreaEdit area => Through(area.Property),
        SetOperation { Properties: { } values } => values
            .Where(v => AreaItemPath.Parse(v.Key) is { } position && !values.Any(w => w.Key.Equals(position.Property, StringComparison.OrdinalIgnoreCase)))
            .Select(v => v.Key).ToList(),
        _ => [],
    };

    /// <summary>The items an area's path goes through: <c>MainArea[0]</c> of <c>MainArea[0].Area</c>.</summary>
    private static List<string> Through(string property)
    {
        var through = new List<string>();
        for (var dot = property.IndexOf('.'); dot > 0; dot = property.IndexOf('.', dot + 1))
        {
            if (property[dot - 1] == ']')
            {
                through.Add(property[..dot]);
            }
        }
        return through;
    }

    /// <summary>
    /// For a step dry-run together with the earlier steps that change its content (<see cref="PlanSimulation"/>): a position
    /// it names (<see cref="Positions"/>) that is, or is inside, a shared block one of those steps makes, as the dry run's
    /// <see cref="WriteOutput.SharedBlocks"/> show it. In one write that block's values could still be changed by its
    /// position; once the earlier step has run, it is content of its own.
    /// </summary>
    /// <returns>The refusal; null when the step names no such block.</returns>
    public static UsageException? OnPlannedBlock(WriteOperation op, WriteOutput? output)
    {
        if (output?.SharedBlocks is not { Count: > 0 } shared)
        {
            return null;
        }
        foreach (var position in Positions(op))
        {
            if (shared.FirstOrDefault(s => s.Action == SharedBlockActions.Created && s.Ref is null
                && (position.Equals(s.Path, StringComparison.OrdinalIgnoreCase) || position.StartsWith(s.Path + ".", StringComparison.OrdinalIgnoreCase))) is { } block)
            {
                return new UsageException(
                    $"{position} names by its position the new block an earlier operation puts at {block.Path}, which the site makes a shared {block.Type} in the content's \"For this page\" folder (its edit UI makes no inline blocks): once that operation has run, the block is content of its own, and its values can't be changed through the area.",
                    "Give the block its values where it is made; or add it with an area add that has an \"id\", and change it with a set on \"$<id>\".");
            }
        }
        return null;
    }
}
