using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptiCli.Core.Sync;

/// <summary>One branch of an item in the target, as a sync compares it.</summary>
/// <param name="Primary">The branch's primary version (the published one, else the common draft): what is compared.</param>
/// <param name="Latest">Its newest version, drafts included: newer than the last sync's is a local change.</param>
/// <param name="LatestSaved">When the newest version was saved: the same id saved at another time is another version (a database restored from a backup reuses ids).</param>
public sealed record TargetBranch(int Primary, bool Published, int Latest, DateTime? LatestSaved = null);

/// <summary>An item the target has (found by the source's GUID).</summary>
/// <param name="Place">Where it is: its parent's GUID, or <c>for:&lt;owner GUID&gt;</c> in a "For this page" folder.</param>
/// <param name="Branches">By language (<c>*</c> for content without languages).</param>
/// <param name="Name">Its name (in its master language), for the result.</param>
public sealed record TargetItem(int Id, Guid Guid, int TypeId, string? Place, bool Deleted, IReadOnlyDictionary<string, TargetBranch> Branches, string? Name = null);

/// <summary>An item the source has, as a sync compares it with the last sync's state.</summary>
/// <param name="Branches">The version read of each branch and whether it is published, by language.</param>
public sealed record SourceItem(string? Place, IReadOnlyDictionary<string, (int Version, bool Published)> Branches);

/// <summary>What a sync does with an item, decided before or after comparing values.</summary>
public enum SyncStep
{
    /// <summary>The target doesn't have it: created.</summary>
    Create,

    /// <summary>In the target's recycle bin and live in the source: moved back out (and updated).</summary>
    Restore,

    /// <summary>The values decide: <see cref="SyncDiff.Compared"/>.</summary>
    Compare,

    /// <summary>The same on both sides, or changed in neither since the last sync.</summary>
    Unchanged,

    /// <summary>Changed in the target since the last sync, and not in the source: the local change stays.</summary>
    Kept,

    /// <summary>Changed on both sides (or trashed locally): listed and left alone, unless <c>--overwrite-local</c>.</summary>
    Conflict,

    /// <summary>Gone from the source (or in its recycle bin) since it was synced: moved to the target's recycle bin with <c>--delete</c>.</summary>
    Trash,

    /// <summary>Gone from the source, and only listed: without <c>--delete</c>, or already in the target's recycle bin.</summary>
    Gone,

    /// <summary>Gone from both: dropped from the sync state.</summary>
    Forget,
}

/// <param name="ConflictIfDifferent">A <see cref="SyncStep.Compare"/> of an item changed on both sides: a conflict unless the values are the same.</param>
/// <param name="Overwrite">The local change is overwritten (<c>--overwrite-local</c>): its steps include other people's drafts.</param>
public sealed record SyncDecision(SyncStep Step, string? Reason = null, bool ConflictIfDifferent = false, bool Overwrite = false);

/// <summary>
/// Decides what a sync does with each item, from what the source and the target have and what the last sync left
/// (<see cref="SyncState"/>). Pure; the values themselves are compared with <see cref="Same"/>.
/// </summary>
/// <remarks>
/// A local change is a branch saved in the target since the last sync (its newest version isn't the one the sync left), a
/// local move, or a local trash of synced content; before the first sync (no state), a draft in the target newer than its
/// published version. A source change is another version read, another publish state, or another place. Changed on both
/// sides with different values is a conflict: listed and skipped, unless <c>--overwrite-local</c>, which writes the source's
/// values over it (the local versions stay in the history). Content only in the target, never synced, isn't looked at.
/// </remarks>
public static partial class SyncDiff
{
    /// <summary>The place of content: <c>for:&lt;owner&gt;</c> in a "For this page" folder, else the parent's GUID.</summary>
    public static string? Place(Guid? parent, Guid? owner) =>
        owner is { } o ? $"for:{o:D}" : parent?.ToString("D");

    /// <summary>The owner a place names (<c>for:&lt;owner&gt;</c>); null for a parent.</summary>
    public static Guid? Owner(string? place) =>
        place is not null && place.StartsWith("for:", StringComparison.Ordinal) && Guid.TryParse(place[4..], out var owner) ? owner : null;

    /// <summary>An item the source has.</summary>
    public static SyncDecision Present(SourceItem source, TargetItem? target, SyncedItem? state, bool overwriteLocal)
    {
        var synced = state is { Trashed: false };
        if (target is null)
        {
            return !synced ? new SyncDecision(SyncStep.Create)
                : overwriteLocal ? new SyncDecision(SyncStep.Create, Overwrite: true)
                : new SyncDecision(SyncStep.Conflict, "synced before, and deleted for good in the target since");
        }
        if (target.Deleted)
        {
            return !synced ? new SyncDecision(SyncStep.Restore)
                : overwriteLocal ? new SyncDecision(SyncStep.Restore, Overwrite: true)
                : new SyncDecision(SyncStep.Conflict, "synced before, and moved to the recycle bin in the target since");
        }
        if (state is null)
        {
            // The baseline: compared, and a draft waiting in the target is a local change the source's values would replace.
            return TargetChanged(target, null) is { } draft
                ? new SyncDecision(SyncStep.Compare, draft, ConflictIfDifferent: true, Overwrite: overwriteLocal)
                : new SyncDecision(SyncStep.Compare);
        }
        var sourceChanged = SourceChanged(source, state);
        var local = TargetChanged(target, state, source.Branches.Keys);
        return (sourceChanged, local) switch
        {
            (false, null) => new SyncDecision(SyncStep.Unchanged),
            (false, { } change) => new SyncDecision(SyncStep.Kept, change),
            (true, null) => new SyncDecision(SyncStep.Compare),
            (true, { } change) => new SyncDecision(SyncStep.Compare, $"changed in the source, and {change}", ConflictIfDifferent: true, Overwrite: overwriteLocal),
        };
    }

    /// <summary>
    /// An item the source no longer has, or has in its recycle bin: synced before (<paramref name="state"/>), or, without a
    /// state, content of the target's whose GUID is in the source's recycle bin, which came from the source.
    /// </summary>
    public static SyncDecision Absent(TargetItem? target, SyncedItem? state, bool delete, bool overwriteLocal)
    {
        if (target is null)
        {
            return new SyncDecision(SyncStep.Forget);
        }
        if (target.Deleted)
        {
            return new SyncDecision(SyncStep.Gone, "deleted in the source; already in the target's recycle bin");
        }
        if (!delete)
        {
            return new SyncDecision(SyncStep.Gone, "deleted in the source (or in its recycle bin); --delete moves it to the target's recycle bin");
        }
        return TargetChanged(target, state) is { } change && !overwriteLocal
            ? new SyncDecision(SyncStep.Conflict, $"deleted in the source, and {change}")
            : new SyncDecision(SyncStep.Trash, Overwrite: overwriteLocal);
    }

    /// <summary>Whether the source has another version, publish state or place for the item than the last sync read.</summary>
    public static bool SourceChanged(SourceItem source, SyncedItem state) =>
        source.Place != state.Source
        || source.Branches.Any(b => !state.Branches.TryGetValue(b.Key, out var synced) || synced.Source != b.Value.Version || synced.Published != b.Value.Published);

    /// <summary>
    /// How the target changed the item since the last sync (a branch saved, moved, restored from the recycle bin the sync
    /// moved it to, or a branch the source writes made there and not by a sync); before the first sync, a draft newer than
    /// the published version. Null: it didn't.
    /// </summary>
    /// <param name="sourceBranches">
    /// The branches the source writes now: one of them that the target has but the last sync didn't record (made locally,
    /// say by a translation) would be written over.
    /// </param>
    public static string? TargetChanged(TargetItem target, SyncedItem? state, IEnumerable<string>? sourceBranches = null)
    {
        if (state is null)
        {
            return target.Branches.FirstOrDefault(b => b.Value.Published && b.Value.Latest > b.Value.Primary) is { Key: not null } draft
                ? $"the target has a newer draft ({Branch(draft.Key)}) than its published version"
                : null;
        }
        if (state.Trashed)
        {
            return "restored in the target after the sync moved it to the recycle bin";
        }
        if (target.Place != state.Target)
        {
            return "moved in the target";
        }
        foreach (var (language, synced) in state.Branches)
        {
            if (!target.Branches.TryGetValue(language, out var branch))
            {
                return $"its {Branch(language)} branch was removed in the target";
            }
            if (branch.Latest != synced.Target || (synced.TargetSaved is { } saved && branch.LatestSaved is { } now && now != saved))
            {
                return $"changed in the target ({Branch(language)})";
            }
        }
        foreach (var language in sourceBranches ?? [])
        {
            if (!state.Branches.ContainsKey(language) && target.Branches.ContainsKey(language))
            {
                return $"its {Branch(language)} branch was made in the target, not by a sync";
            }
        }
        return null;
    }

    /// <summary>
    /// The target's version of a branch a sync found the same as the source's, as the state records it: the newest one,
    /// except that a draft newer than the published version (which isn't what was compared) is left out, so it stays a local
    /// change.
    /// </summary>
    public static SyncedBranch InStep(int sourceVersion, bool sourcePublished, TargetBranch branch) =>
        branch.Published && branch.Latest > branch.Primary
            ? new SyncedBranch(sourceVersion, sourcePublished, branch.Primary)
            : new SyncedBranch(sourceVersion, sourcePublished, branch.Latest, branch.LatestSaved);

    private static string Branch(string language) => language == "*" ? "the item" : language;

    /// <summary>
    /// After comparing values (<see cref="Same"/>), what a <see cref="SyncStep.Compare"/> comes to: unchanged, a conflict, or
    /// a change to write (<paramref name="moved"/> and/or <paramref name="different"/>).
    /// </summary>
    public static SyncDecision Compared(SyncDecision decision, bool different, bool moved) =>
        !different && !moved ? new SyncDecision(SyncStep.Unchanged)
        : decision.ConflictIfDifferent && !decision.Overwrite ? new SyncDecision(SyncStep.Conflict, decision.Reason)
        : decision;

    /// <summary>
    /// Whether the target's branches have everything the source's write as the source has it: name, publish state and every
    /// value the source writes (from <c>ExportPlanBuilder.Comparable</c>). Values the target has besides (properties only
    /// its model has, values the source leaves out) don't count; a branch only the target has doesn't either. The GUID of a
    /// "For this page" folder in a URL is each database's own and is ignored.
    /// </summary>
    /// <returns>The first branch that differs (<c>*</c> for content without languages); null when none does.</returns>
    public static string? Same(IReadOnlyDictionary<string, JsonObject> source, IReadOnlyDictionary<string, JsonObject> target)
    {
        foreach (var (language, branch) in source)
        {
            if (!target.TryGetValue(language, out var other)
                || (string?)branch["name"] != (string?)other["name"]
                || (bool?)branch["published"] != (bool?)other["published"])
            {
                return language;
            }
            var theirs = other["values"] as JsonObject ?? [];
            foreach (var (name, value) in branch["values"] as JsonObject ?? [])
            {
                if (!JsonNode.DeepEquals(Normalized(value), Normalized(theirs.TryGetPropertyValue(name, out var found) ? found : null)))
                {
                    return language;
                }
            }
        }
        return null;
    }

    /// <summary>A value with what is each database's own taken out: the GUID of a "For this page" folder in a URL.</summary>
    private static JsonNode? Normalized(JsonNode? value) => value switch
    {
        JsonValue leaf when leaf.TryGetValue<string>(out var text) => JsonValue.Create(AssetFolder().Replace(text, "/contentassets/*/")),
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Normalized(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Normalized).ToArray()),
        _ => value?.DeepClone(),
    };

    [GeneratedRegex("/contentassets/[0-9a-fA-F]{32}/", RegexOptions.IgnoreCase)]
    private static partial Regex AssetFolder();
}
