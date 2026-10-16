using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Export;
using OptiCli.Core.Output;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Sync;

/// <param name="Delete">Move content synced before and gone from the source to the target's recycle bin (<c>--delete</c>).</param>
/// <param name="OverwriteLocal">Write the source's values over local changes instead of listing them as conflicts.</param>
/// <param name="Strict">Fail when the source's model has types or properties the target's build lacks, instead of leaving them out.</param>
public sealed record SyncOptions(bool Delete, bool OverwriteLocal, bool Strict, bool DryRun);

/// <summary>An item a sync lists.</summary>
/// <param name="Ref">The target's ref, when the target has it (after the sync, for new content); null otherwise.</param>
/// <param name="Reason">Why it is listed (conflicts, skipped, kept, gone), or what changed.</param>
public sealed record SyncEntry(Guid Guid, string? Ref, string? Type, string? Name, string? Reason = null);

public sealed record SyncDatabase(string? Server, string? Database, string? Role);

public sealed record SyncCounts(int Created, int Updated, int Moved, int Restored, int Trashed, int Unchanged, int Kept, int Conflicts, int Gone, int Skipped);

/// <param name="Count">Media files the plan uploads.</param>
public sealed record SyncFiles(int Count, long Bytes, string? From);

/// <param name="Baseline">The first sync of this source into this target: every item was compared, and the state taken.</param>
/// <param name="State">The sync state's file (unchanged by a dry run).</param>
/// <param name="Steps">The plan's operations; 0 when nothing differs, and then nothing is applied.</param>
/// <param name="Plan">The plan's folder, when it was kept (<c>--out</c>).</param>
/// <param name="Run">What <c>apply</c> did with the plan (each step's status, results and undo hints); null when there was nothing to apply.</param>
/// <param name="Model">How the source's content model differs from the target's for the content synced: types skipped, properties dropped.</param>
public sealed record SyncResult(
    SyncDatabase Source,
    SyncDatabase Target,
    bool DryRun,
    bool Baseline,
    string State,
    SyncCounts Counts,
    IReadOnlyList<SyncEntry> Created,
    IReadOnlyList<SyncEntry> Updated,
    IReadOnlyList<SyncEntry> Moved,
    IReadOnlyList<SyncEntry> Restored,
    IReadOnlyList<SyncEntry> Trashed,
    IReadOnlyList<SyncEntry> Conflicts,
    IReadOnlyList<SyncEntry> Kept,
    IReadOnlyList<SyncEntry> Gone,
    IReadOnlyList<SyncEntry> Skipped,
    IReadOnlyList<DroppedValue> Dropped,
    IReadOnlyList<LeftOutDraft> Drafts,
    IReadOnlyList<ModelDifference> Model,
    SyncFiles Files,
    int Steps,
    string? Plan,
    PlanRun? Run,
    IReadOnlyList<string> NotSynced);

/// <summary>
/// <c>sync pull</c>: brings the content a source database has into the target (local) database, writing only what
/// differs. It is an export of the source (<see cref="ExportReader"/>, read over SQL only), a diff against the target by
/// GUID (<see cref="SyncDiff"/>, against the last sync's <see cref="SyncState"/>), and an <c>apply --update-existing</c> of a
/// plan with only the changes (<see cref="PlanRunner"/>, through the target's site): content the target lacks is
/// created, content that differs updated (a new version; published when the source's branch is), content the source moved
/// moved (a <c>move</c> step, then the update), content in the target's recycle bin restored, and with
/// <see cref="SyncOptions.Delete"/> content synced before and gone from the source moved to the recycle bin (never deleted
/// for good). Content only in the target, never synced, is left alone. An empty diff doesn't apply anything.
/// </summary>
/// <param name="folder">Where the plan (and the media files it uploads) is written.</param>
/// <param name="blobs">Where the source's media files are; null leaves media that would be written out (listed).</param>
/// <param name="targetBlobs">The target site's file blob folder, to compare a media item's file without uploading it; null: uploaded, and the site compares.</param>
public sealed class SyncPull(ContentSession source, ContentSession target, WriteExecutor writes, SyncState state, string folder, BlobSource? blobs, string? targetBlobs)
{
    /// <summary>What a sync never carries over, said in every result.</summary>
    public static readonly IReadOnlyList<string> NotSynced =
    [
        "access rights, versions other than the one written per branch, and who saved them when (the target's history says the sync wrote it)",
        "content only in the target that was never synced, and language branches only the target has",
        "content in the source's recycle bin, Forms submissions, visitor groups, categories, users and everything else that isn't content",
        "the database schema and content types: the target's build decides those (a bacpac copies everything)",
    ];

    /// <summary>Called as the sync goes (<c>reading</c>, <c>comparing</c>, then <c>checking</c> and <c>applying</c> steps), with how many of how many.</summary>
    public Action<string, int, int>? Progress { get; init; }

    /// <summary>Keep the plan's folder (<c>--out</c>); otherwise it is the caller's to remove.</summary>
    public bool KeepPlan { get; init; }

    private sealed record Planned(ExportCandidate Candidate, ExportItem Item, SyncDecision Decision, bool Moved, bool Different);

    /// <param name="roots">What the pull covers in the source.</param>
    /// <param name="languages">The source's language ids to write (besides each item's master branch); null for all.</param>
    /// <exception cref="ConflictException">With <see cref="SyncOptions.Strict"/>, the models differ (<c>details.reason</c>: <c>modelDiffers</c>).</exception>
    /// <exception cref="OptiCliException">
    /// The plan failed validation (nothing written) or a step failed: <c>details</c> is the <see cref="SyncResult"/>, whose
    /// <c>run</c> lists what was saved with undo hints. The sync state has what was saved.
    /// </exception>
    public async Task<SyncResult> RunAsync(IReadOnlyList<ExportRoot> roots, IReadOnlySet<int>? languages, SyncOptions options, SyncDatabase from, SyncDatabase to, CancellationToken cancellationToken)
    {
        var readAt = DateTime.UtcNow;
        var baseline = state.Synced is null;
        var model = await ModelCompatibility.CompareAsync(source, target, cancellationToken);
        var selection = await new ExportReader(source).SelectAsync(roots, languages, cancellationToken);
        var differences = model.Differences(source.Model, selection.Items.Select(c => c.Header.TypeId));
        if (options.Strict && differences.Count > 0)
        {
            throw new ConflictException(
                $"The source's content model has {differences.Count} type(s) or properties the target's build lacks or has otherwise (--strict): {string.Join("; ", differences.Take(5).Select(Describe))}{(differences.Count > 5 ? "; …" : "")}.",
                "details.differences lists them. Bring the target's code up to the source's (a branch with those types), or sync without --strict: the content of a missing type is then skipped and missing properties dropped, each listed.")
            {
                Details = new { reason = "modelDiffers", differences },
            };
        }

        var skipped = selection.Skipped.Select(s => new SyncEntry(s.Guid, null, s.Type, s.Name, s.Reason)).ToList();
        var candidates = new List<ExportCandidate>();
        foreach (var candidate in selection.Items)
        {
            if (model.MissingType(candidate.Header.TypeId) is { } missing)
            {
                skipped.Add(Entry(candidate, null, $"its type {candidate.Type} is {missing}"));
                continue;
            }
            candidates.Add(candidate);
        }
        var bySource = candidates.ToDictionary(c => c.Guid);
        var selected = selection.Items.Select(c => c.Guid).Concat(selection.Skipped.Select(s => s.Guid)).ToHashSet();

        // The target's side of everything selected, synced before, or in the source's recycle bin.
        var binned = await SourceRecycleBinAsync(cancellationToken);
        var targets = await TargetItemsAsync(candidates.Select(c => c.Guid).Concat(state.Items.Keys).Concat(binned), cancellationToken);
        var gone = await GoneAsync(state.Items.Keys.Concat(binned.Where(targets.ContainsKey)).Where(g => !selected.Contains(g)).Distinct().ToList(), roots, targets, cancellationToken);

        var decisions = new Dictionary<Guid, SyncDecision>();
        foreach (var candidate in candidates)
        {
            var there = targets.GetValueOrDefault(candidate.Guid);
            if (there is not null && model.TargetType(candidate.Header.TypeId)?.Id is { } typeId && there.TypeId != typeId)
            {
                skipped.Add(Entry(candidate, there, $"it is a {target.Model.TypeName(there.TypeId)} in the target, a {candidate.Type} in the source; a sync doesn't change the type of content"));
                continue;
            }
            decisions[candidate.Guid] = SyncDiff.Present(SourceOf(candidate), there, state.Items.GetValueOrDefault(candidate.Guid), options.OverwriteLocal);
        }
        foreach (var guid in gone)
        {
            decisions[guid] = SyncDiff.Absent(targets.GetValueOrDefault(guid), state.Items.GetValueOrDefault(guid), options.Delete, options.OverwriteLocal);
        }

        // The source's values, only of what may be written.
        var toRead = candidates.Where(c => decisions.GetValueOrDefault(c.Guid)?.Step is SyncStep.Create or SyncStep.Restore or SyncStep.Compare).ToList();
        var read = await new ExportReader(source).ReadAsync(toRead, cancellationToken, (done, total) => Progress?.Invoke("reading", done, total));
        var items = read.Items.ToDictionary(i => i.Guid, i => Fit(i, bySource[i.Guid], model));
        var drafts = await DraftsAsync(candidates, cancellationToken);
        var dropped = read.Dropped.ToList();

        // Media: the file goes in the plan's folder; without it, new media can't be written. Media to compare is copied only
        // when its file may differ (below).
        var files = new SyncFiles(0, 0, blobs?.Description);
        var hashes = new Dictionary<Guid, string>();
        async Task<string?> CopyAsync(ExportItem item)
        {
            var uri = read.BlobUris.GetValueOrDefault(item.Guid);
            var problem = uri is null ? "it has no file (no blob URI)" : blobs is null ? "no --blobs to read its file from" : null;
            if (problem is not null)
            {
                return problem;
            }
            var relative = ExportPlanBuilder.FilePath(item.Guid, ExportFiles.FileName((string?)item.Branches[0].Document["name"], uri!));
            var destination = Path.Combine(folder, relative);
            problem = await blobs!.CopyAsync(uri!, destination, cancellationToken);
            if (problem is null)
            {
                items[item.Guid] = item with { File = relative };
                hashes[item.Guid] = await HashAsync(destination, cancellationToken);
                files = files with { Count = files.Count + 1, Bytes = files.Bytes + new FileInfo(destination).Length };
            }
            return problem;
        }
        void SkipMedia(Guid guid, string problem)
        {
            items.Remove(guid);
            decisions.Remove(guid);
            skipped.Add(Entry(bySource[guid], targets.GetValueOrDefault(guid), $"media without its file: {problem}"));
        }
        foreach (var item in items.Values.Where(i => i.Kind == ContentKind.Media && decisions[i.Guid].Step is SyncStep.Create or SyncStep.Restore).ToList())
        {
            if (await CopyAsync(item) is { } problem)
            {
                SkipMedia(item.Guid, problem);
            }
        }

        // What the target will have: what it has (outside the recycle bin or in it), and what the sync creates or restores,
        // as long as its place is there. Placing an item can depend on another being created first.
        var known = await TargetHeadersAsync(items.Values.SelectMany(i => new[] { i.Parent, i.Owner }.OfType<Guid>())
            .Concat(ExportPlanBuilder.Referenced(items.Values)).Concat(items.Keys), cancellationToken);
        var creating = items.Keys.Where(g => decisions[g].Step is SyncStep.Create or SyncStep.Restore).ToHashSet();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var guid in creating.ToList())
            {
                var place = bySource[guid].Owner ?? bySource[guid].Parent;
                if (place is { } at && !creating.Contains(at) && !(known.TryGetValue(at, out var header) && !header.Deleted))
                {
                    creating.Remove(guid);
                    decisions.Remove(guid);
                    skipped.Add(Entry(bySource[guid], targets.GetValueOrDefault(guid), known.TryGetValue(at, out var trashed) && trashed.Deleted
                        ? $"its {(bySource[guid].Owner is null ? "parent" : "owner")} {at:D} is in the target's recycle bin"
                        : $"its {(bySource[guid].Owner is null ? "parent" : "owner")} {at:D} isn't in the target, and the sync doesn't bring it (not in what it covers, skipped, or a conflict)"));
                    changed = true;
                }
            }
        }
        var targetFacts = new ExportTarget(
            g => creating.Contains(g) || known.ContainsKey(g),
            id => !Guid.TryParse(id, out var group) || target.Model.VisitorGroups.ContainsKey(group),
            name => target.Model.CategoryNames.Contains(name, StringComparer.OrdinalIgnoreCase));

        // Compare what may have changed with the target's values.
        var comparing = items.Keys.Where(g => decisions.GetValueOrDefault(g)?.Step == SyncStep.Compare).ToList();
        var (theirs, theirFiles) = await TargetReadAsync(comparing.Select(g => targets[g].Id).ToList(), cancellationToken);
        var folders = await AssetFoldersAsync(comparing.Select(g => bySource[g].Owner).OfType<Guid>(), cancellationToken);
        var planned = new List<Planned>();
        var compared = 0;
        foreach (var guid in items.Keys.ToList())
        {
            var decision = decisions.GetValueOrDefault(guid);
            if (decision is null)
            {
                continue;
            }
            var candidate = bySource[guid];
            var item = items[guid];
            if (decision.Step is SyncStep.Create or SyncStep.Restore)
            {
                planned.Add(new Planned(candidate, item, decision, Moved: false, Different: true));
                continue;
            }
            var there = targets[guid];
            var place = SyncDiff.Place(candidate.Parent, candidate.Owner);
            var moved = place != there.Place;
            var different = !theirs.TryGetValue(guid, out var mine)
                || SyncDiff.Same(ExportPlanBuilder.Comparable(item, targetFacts), ExportPlanBuilder.Comparable(mine)) is not null;
            if (item.Kind == ContentKind.Media && read.BlobUris.GetValueOrDefault(guid) is var sourceFile && (sourceFile is null || sourceFile != theirFiles.GetValueOrDefault(guid)))
            {
                // Another stored file than the target's (the same one is: a copy of the database): its bytes tell.
                if (await CopyAsync(item) is { } problem)
                {
                    SkipMedia(guid, problem);
                    continue;
                }
                item = items[guid];
                different |= !SameFile(hashes.GetValueOrDefault(guid), TargetBlob(theirFiles.GetValueOrDefault(guid)));
            }
            var outcome = SyncDiff.Compared(decision, different, moved);
            Progress?.Invoke("comparing", ++compared, comparing.Count);
            if (outcome.Step != SyncStep.Compare)
            {
                decisions[guid] = outcome;
                continue;
            }
            if (moved && Unplaceable(candidate, creating, known, folders) is { } why)
            {
                decisions.Remove(guid);
                skipped.Add(Entry(candidate, there, why));
                continue;
            }
            if (item.Kind == ContentKind.Media && item.File is null)
            {
                // The same file, other values: the upload step still takes the file (the site keeps it, as it is the same).
                if (await CopyAsync(item) is { } problem)
                {
                    SkipMedia(guid, problem);
                    continue;
                }
                item = items[guid];
            }
            planned.Add(new Planned(candidate, item, outcome, moved, different));
        }

        // The plan: the export's steps for what changed, a move before each moved item's, the trash last.
        var built = ExportPlanBuilder.Build(planned.Select(p => p.Item).ToList(), target: targetFacts, now: readAt);
        dropped.AddRange(built.Dropped);
        foreach (var (guid, reason) in built.Skipped)
        {
            planned.RemoveAll(p => p.Item.Guid == guid);
            decisions.Remove(guid);
            skipped.Add(Entry(bySource[guid], targets.GetValueOrDefault(guid), reason));
        }
        var operations = built.Operations.Select(o => o!.DeepClone().AsObject()).ToList();
        var plannedIds = planned.Select(p => p.Item.Guid).ToHashSet();
        foreach (var move in planned.Where(p => p.Moved))
        {
            var at = operations.FindIndex(o => (string?)o["guid"] == move.Item.Guid.ToString("D"));
            var destination = move.Candidate.Owner is { } owner ? folders[owner].ToString("D")
                : plannedIds.Contains(move.Candidate.Parent!.Value) ? $"${ExportPlanBuilder.PlanId(move.Candidate.Parent.Value)}"
                : move.Candidate.Parent.Value.ToString("D");
            operations.Insert(at, new JsonObject { ["op"] = "move", ["ref"] = move.Item.Guid.ToString("D"), ["to"] = destination });
        }
        foreach (var overwrite in planned.Where(p => p.Decision.Overwrite))
        {
            foreach (var operation in operations.Where(o => GuidOf(o) == overwrite.Item.Guid && (string?)o["op"] is not "move"))
            {
                // The source's values go over the local changes, other people's drafts included (they stay in the history).
                operation["includeDraft"] = true;
            }
        }
        var trashing = TopMost(decisions.Where(d => d.Value.Step == SyncStep.Trash).Select(d => d.Key).ToList(), targets);
        foreach (var guid in trashing)
        {
            // The source deleted it with whatever still refers to it; the target's references then point into the recycle bin too.
            operations.Add(new JsonObject { ["op"] = "delete", ["ref"] = guid.ToString("D"), ["ignoreReferences"] = true });
        }

        await WritePlanAsync(operations, built.Texts, from, readAt, cancellationToken);
        PlanRun? run = null;
        OptiCliException? failure = null;
        if (operations.Count > 0)
        {
            var plan = WritePlan.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "plan.json"), cancellationToken));
            var runner = new PlanRunner(target, writes, folder) { MovesFirst = true, Progress = Progress };
            try
            {
                run = await runner.RunAsync(plan, options.DryRun, publishAll: false, cancellationToken);
            }
            catch (OptiCliException ex) when (ex.Details is PlanRun partial)
            {
                run = partial;
                failure = ex;
            }
        }

        // Only what was saved (or found the same) goes in the state: a stopped or failed run leaves the rest to the next sync.
        // Recorded even after Ctrl+C (a few queries), so what was saved isn't taken for a local change next time.
        var done = Done(run, operations);
        if (!options.DryRun && (run is null || !run.DryRun))
        {
            await RecordAsync(decisions, planned, trashing, done, bySource, readAt, CancellationToken.None);
        }

        var result = await ResultAsync(options, from, to, baseline, decisions, planned, trashing, done, run, skipped, dropped, drafts, differences, files, operations.Count, targets, bySource, CancellationToken.None);
        if (failure is not null)
        {
            throw OptiCliException.Create(failure.Code, failure.Message, failure.Hint, result);
        }
        return result;
    }

    /// <summary>
    /// Every selected branch whose source has a newer draft than the published version a sync writes, whether or not the
    /// item changed: what the target won't have until it is published in the source.
    /// </summary>
    private async Task<List<LeftOutDraft>> DraftsAsync(IReadOnlyList<ExportCandidate> candidates, CancellationToken cancellationToken)
    {
        var unpublished = await ContentHeaderReader.NewestUnpublishedAsync(source.Db, candidates.Where(c => c.Branches.Any(b => b.Published)).Select(c => c.Id), cancellationToken);
        return candidates.SelectMany(c => c.Branches
                .Where(b => b.Published && unpublished.TryGetValue((c.Id, b.LanguageId), out var draft) && draft > b.VersionId)
                .Select(b => new LeftOutDraft(ContentIdentity.RefFor(c.Id), c.Guid, c.Header.Languages.GetValueOrDefault(b.LanguageId)?.Name, b.Language,
                    ContentIdentity.RefFor(c.Id, unpublished[(c.Id, b.LanguageId)]))))
            .ToList();
    }

    private static string Describe(ModelDifference difference) =>
        $"{difference.Type}{(difference.Property is null ? "" : $".{difference.Property}")} ({difference.Difference})";

    private static SourceItem SourceOf(ExportCandidate candidate) => new(
        SyncDiff.Place(candidate.Parent, candidate.Owner),
        candidate.Branches.ToDictionary(b => b.Language ?? "*", b => (b.VersionId, b.Published), StringComparer.OrdinalIgnoreCase));

    private static SyncEntry Entry(ExportCandidate candidate, TargetItem? there, string? reason) =>
        new(candidate.Guid, there is null ? null : ContentIdentity.RefFor(there.Id), candidate.Type, candidate.Header.LanguageRow(null)?.Name, reason);

    /// <summary>
    /// The item as the target's build takes it: its type by the target's name (matched by GUID, so a renamed type still
    /// matches), without the properties the target lacks or has as another type (<see cref="ModelCompatibility"/>, listed there).
    /// </summary>
    private static ExportItem Fit(ExportItem item, ExportCandidate candidate, ModelCompatibility model)
    {
        var drop = model.DroppedProperties(candidate.Header.TypeId);
        foreach (var branch in item.Branches)
        {
            if (branch.Document["properties"] is JsonObject properties)
            {
                foreach (var name in properties.Select(p => p.Key).Where(drop.ContainsKey).ToList())
                {
                    properties.Remove(name);
                }
            }
        }
        return model.TargetType(candidate.Header.TypeId) is { } type && type.Name != item.Type ? item with { Type = type.Name } : item;
    }

    /// <summary>The GUID of the item a plan step writes: its own, or the one its <c>$id</c> or GUID ref names.</summary>
    private static Guid? GuidOf(JsonObject operation)
    {
        if (Guid.TryParse((string?)operation["guid"], out var own))
        {
            return own;
        }
        var reference = (string?)operation["ref"];
        return reference is null ? null
            : reference.StartsWith("$c", StringComparison.Ordinal) && Guid.TryParseExact(reference[2..], "N", out var planned) ? planned
            : Guid.TryParse(reference, out var named) ? named
            : null;
    }

    /// <summary>
    /// Why a moved item can't be placed where the source has it: its new parent isn't in the target (or in its recycle bin)
    /// and the sync doesn't create it, or its new "For this page" folder doesn't exist there. Null when it can.
    /// </summary>
    private static string? Unplaceable(ExportCandidate candidate, IReadOnlySet<Guid> creating, IReadOnlyDictionary<Guid, ContentHeader> known, IReadOnlyDictionary<Guid, Guid> folders)
    {
        if (candidate.Owner is { } owner)
        {
            return folders.ContainsKey(owner) ? null
                : $"moved in the source to the \"For this page\" folder of {owner:D}, which the target doesn't have yet (the next sync moves it once it does)";
        }
        return candidate.Parent is { } parent && !creating.Contains(parent) && !(known.TryGetValue(parent, out var header) && !header.Deleted)
            ? $"moved in the source under {parent:D}, which the target doesn't have (or has in its recycle bin)"
            : null;
    }

    /// <summary>The target's items with these GUIDs (outside the recycle bin or in it): where each is and its branches' versions.</summary>
    private async Task<Dictionary<Guid, TargetItem>> TargetItemsAsync(IEnumerable<Guid> guids, CancellationToken cancellationToken)
    {
        var headers = await TargetHeadersAsync(guids, cancellationToken);
        var latest = await VersionReader.LatestIdsAsync(target.Db, target.Model, headers.Values.Select(h => h.Id), cancellationToken);
        var places = await PlacesAsync(target.Db, headers.Values.Select(h => h.ParentId).OfType<int>(), cancellationToken);
        return headers.Values.ToDictionary(h => h.Guid, h => new TargetItem(
            h.Id,
            h.Guid,
            h.TypeId,
            h.ParentId is { } parent && places.TryGetValue(parent, out var place) ? place : null,
            h.Deleted,
            h.Languages.Where(l => l.Value.VersionId is not null).ToDictionary(
                l => target.Model.Language(l.Key) is { IsInvariant: false } language ? language.DisplayCode ?? "*" : "*",
                l => new TargetBranch(l.Value.VersionId!.Value, l.Value.Status == VersionStatus.Published, latest.GetValueOrDefault((h.Id, l.Key), l.Value.VersionId!.Value)),
                StringComparer.OrdinalIgnoreCase)));
    }

    private async Task<IReadOnlyDictionary<Guid, ContentHeader>> TargetHeadersAsync(IEnumerable<Guid> guids, CancellationToken cancellationToken)
    {
        var ids = await ContentHeaderReader.IdsByGuidsAsync(target.Db, guids.Distinct(), cancellationToken);
        return (await ContentHeaderReader.ByIdsAsync(target.Db, ids.Values, cancellationToken)).Values.ToDictionary(h => h.Guid);
    }

    /// <summary>For each parent id, the place of content below it: its GUID, or <c>for:&lt;owner&gt;</c> for a "For this page" folder.</summary>
    private static async Task<IReadOnlyDictionary<int, string?>> PlacesAsync(CmsDatabase db, IEnumerable<int> parents, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string?>();
        foreach (var list in SqlLists.Ints(parents))
        {
            var rows = await db.QueryAsync($"SELECT pkID, ContentGUID, ContentOwnerID FROM tblContent WHERE pkID IN ({list})",
                r => (Id: r.GetInt32("pkID"), Guid: r.GetGuid("ContentGUID"), Owner: r.GetGuidOrNull("ContentOwnerID")), cancellationToken);
            foreach (var row in rows)
            {
                result[row.Id] = SyncDiff.Place(row.Guid, row.Owner is { } owner && owner != Guid.Empty ? owner : null);
            }
        }
        return result;
    }

    /// <summary>The target's "For this page" folder of each owner that has one (outside the recycle bin): owner GUID to folder GUID.</summary>
    private async Task<IReadOnlyDictionary<Guid, Guid>> AssetFoldersAsync(IEnumerable<Guid> owners, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, Guid>();
        foreach (var list in SqlLists.Guids(owners))
        {
            var rows = await target.Db.QueryAsync($"""
                SELECT o.ContentGUID AS Owner, f.ContentGUID AS Folder
                FROM tblContent o JOIN tblContent f ON f.ContentGUID = o.ContentAssetsID
                WHERE o.ContentGUID IN ({list}) AND o.Deleted = 0 AND f.Deleted = 0
                """, r => (Owner: r.GetGuid("Owner"), Folder: r.GetGuid("Folder")), cancellationToken);
            foreach (var row in rows)
            {
                result[row.Owner] = row.Folder;
            }
        }
        return result;
    }

    /// <summary>
    /// Of the items synced before that this pull didn't select, and of the target's items the source has in its recycle bin:
    /// those the source no longer has (or has in its recycle bin), and that are within what the pull covers in the target
    /// (the roots by GUID there, and what is below them; "For this page" content with its owner).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> GoneAsync(IReadOnlyList<Guid> others, IReadOnlyList<ExportRoot> roots, IReadOnlyDictionary<Guid, TargetItem> targets, CancellationToken cancellationToken)
    {
        if (others.Count == 0)
        {
            return [];
        }
        var ids = await ContentHeaderReader.IdsByGuidsAsync(source.Db, others, cancellationToken);
        var headers = await ContentHeaderReader.ByIdsAsync(source.Db, ids.Values, cancellationToken);
        var gone = others.Where(g => !ids.TryGetValue(g, out var id) || !headers.TryGetValue(id, out var header) || header.Deleted).ToList();
        if (gone.Count == 0)
        {
            return gone;
        }

        // The roots by GUID in the target, and the target's items with their ancestors (and their owners', for "For this page" content).
        var rootHeaders = await ContentHeaderReader.ByIdsAsync(source.Db, roots.Select(r => r.Id), cancellationToken);
        var inTarget = await TargetHeadersAsync(rootHeaders.Values.Select(h => h.Guid), cancellationToken);
        var rootIds = roots.Where(r => rootHeaders.ContainsKey(r.Id) && inTarget.ContainsKey(rootHeaders[r.Id].Guid))
            .Select(r => (Id: inTarget[rootHeaders[r.Id].Guid].Id, r.IncludeSelf, r.Descendants)).ToList();
        var items = await TargetHeadersAsync(gone, cancellationToken);
        var owners = await TargetHeadersAsync(gone.Select(g => SyncDiff.Owner(targets.GetValueOrDefault(g)?.Place)).OfType<Guid>(), cancellationToken);
        bool Within(ContentHeader header) => rootIds.Any(r => (r.IncludeSelf && r.Id == header.Id) || (r.Descendants && header.AncestorIds.Contains(r.Id)));
        return gone.Where(g => items.TryGetValue(g, out var header)
            && (Within(header) || (SyncDiff.Owner(targets.GetValueOrDefault(g)?.Place) is { } owner && owners.TryGetValue(owner, out var ownerHeader) && Within(ownerHeader)))).ToList();
    }

    /// <summary>
    /// The GUIDs of everything in the source's recycle bin. The target's content with one of them came from the source (a
    /// GUID is the content's own), so it counts as synced before even on the first sync: the source deleted it.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> SourceRecycleBinAsync(CancellationToken cancellationToken) =>
        await source.Db.QueryAsync("SELECT ContentGUID FROM tblContent WHERE Deleted = 1", r => r.GetGuid("ContentGUID"), cancellationToken);

    /// <summary>The items to move to the recycle bin without those below another of them (which go with it).</summary>
    private static IReadOnlyList<Guid> TopMost(IReadOnlyList<Guid> trash, IReadOnlyDictionary<Guid, TargetItem> targets) =>
        trash.Where(g => !(Guid.TryParse(targets[g].Place, out var parent) && trash.Contains(parent))).ToList();

    /// <summary>The target's values of items (as the source's are read), for comparing.</summary>
    /// <returns>The items by GUID, and each media item's blob URI.</returns>
    private async Task<(IReadOnlyDictionary<Guid, ExportItem> Items, IReadOnlyDictionary<Guid, string> Files)> TargetReadAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var reader = new ExportReader(target);
        var selection = await reader.CandidatesAsync(ids, null, cancellationToken);
        var read = await reader.ReadAsync(selection.Items, cancellationToken, (done, total) => Progress?.Invoke("reading the target", done, total));
        return (read.Items.ToDictionary(i => i.Guid), read.BlobUris);
    }

    /// <summary>The target's file of a media item (by its blob URI), in the target site's blob folder; null when it isn't there.</summary>
    private string? TargetBlob(string? uri)
    {
        if (targetBlobs is null || uri is null || Queries.BlobLocator.ParseUri(uri) is not { Container: { } container, File: { } file })
        {
            return null;
        }
        var path = Path.Combine(targetBlobs, container, file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Whether the source's file (its hash) is the target's; false when the target's file can't be read here (the site then compares).</summary>
    private static bool SameFile(string? sourceHash, string? targetFile) =>
        sourceHash is not null && targetFile is not null && Hash(targetFile) == sourceHash;

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private async Task WritePlanAsync(List<JsonObject> operations, IReadOnlyDictionary<string, string> texts, SyncDatabase from, DateTime readAt, CancellationToken cancellationToken)
    {
        foreach (var (path, text) in texts)
        {
            var file = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), cancellationToken);
        }
        var document = new JsonObject
        {
            [WritePlan.SourceField] = new JsonObject
            {
                ["server"] = from.Server,
                ["database"] = from.Database,
                ["role"] = from.Role,
                ["readAt"] = readAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["sync"] = true,
            },
            ["operations"] = new JsonArray(operations.Select(o => (JsonNode)o).ToArray()),
        };
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "plan.json"), document.ToJsonString(PlanJson) + "\n", new UTF8Encoding(false), cancellationToken);
    }

    private static readonly JsonSerializerOptions PlanJson = new(JsonOutput.Options) { WriteIndented = true };

    /// <summary>
    /// The items whose every step ran and was saved or found the same, and whether one of them saved something; none for a
    /// dry run (nothing ran).
    /// </summary>
    private static IReadOnlyDictionary<Guid, bool> Done(PlanRun? run, IReadOnlyList<JsonObject> operations)
    {
        var done = new Dictionary<Guid, bool>();
        if (run is null || run.DryRun)
        {
            return done;
        }
        var statuses = run.Operations.ToDictionary(o => o.Index, o => o.Status);
        foreach (var group in operations.Select((o, i) => (Guid: GuidOf(o), Status: statuses.GetValueOrDefault(i, PlanStepStatus.NotRun))).Where(s => s.Guid is not null).GroupBy(s => s.Guid!.Value))
        {
            if (group.All(s => s.Status is PlanStepStatus.Saved or PlanStepStatus.Unchanged))
            {
                done[group.Key] = group.Any(s => s.Status == PlanStepStatus.Saved);
            }
        }
        return done;
    }

    /// <summary>
    /// The state after the sync: what is the same on both sides now (found the same, or written), as both have it; what the
    /// sync moved to the recycle bin; nothing for conflicts, local changes kept, skipped items, or steps that didn't run.
    /// </summary>
    private async Task RecordAsync(Dictionary<Guid, SyncDecision> decisions, IReadOnlyList<Planned> planned, IReadOnlyList<Guid> trashing,
        IReadOnlyDictionary<Guid, bool> done, IReadOnlyDictionary<Guid, ExportCandidate> bySource, DateTime readAt, CancellationToken cancellationToken)
    {
        var plannedIds = planned.Select(p => p.Item.Guid).ToHashSet();
        var record = decisions.Where(d => d.Value.Step == SyncStep.Unchanged && bySource.ContainsKey(d.Key)).Select(d => d.Key)
            .Concat(plannedIds.Where(done.ContainsKey))
            .ToList();
        var now = await TargetItemsAsync(record.Concat(trashing).Concat(decisions.Where(d => d.Value.Step == SyncStep.Gone).Select(d => d.Key)), cancellationToken);
        foreach (var guid in record)
        {
            if (!now.TryGetValue(guid, out var there))
            {
                continue;
            }
            var candidate = bySource[guid];
            var branches = new Dictionary<string, SyncedBranch>(state.Items.GetValueOrDefault(guid)?.Branches ?? new Dictionary<string, SyncedBranch>(), StringComparer.OrdinalIgnoreCase);
            foreach (var branch in candidate.Branches)
            {
                var language = branch.Language ?? "*";
                branches[language] = new SyncedBranch(branch.VersionId, branch.Published, there.Branches.GetValueOrDefault(language)?.Latest ?? 0);
            }
            state.Items[guid] = new SyncedItem(SyncDiff.Place(candidate.Parent, candidate.Owner), there.Place, branches);
        }
        foreach (var guid in decisions.Where(d => d.Value.Step == SyncStep.Trash).Select(d => d.Key))
        {
            // Below a trashed item, it went to the recycle bin with it.
            if (now.TryGetValue(guid, out var there) && there.Deleted)
            {
                state.Items[guid] = state.Items.TryGetValue(guid, out var synced)
                    ? synced with { Trashed = true }
                    : new SyncedItem(null, null, new Dictionary<string, SyncedBranch>(), Trashed: true);
            }
        }
        foreach (var (guid, decision) in decisions)
        {
            if (decision.Step == SyncStep.Forget)
            {
                state.Items.Remove(guid);
            }
            else if (decision.Step == SyncStep.Gone && now.TryGetValue(guid, out var there) && there.Deleted && state.Items.TryGetValue(guid, out var synced))
            {
                // Gone from both: should the source bring it back, it isn't a local change.
                state.Items[guid] = synced with { Trashed = true };
            }
        }
        state.Save(readAt);
    }

    private async Task<SyncResult> ResultAsync(SyncOptions options, SyncDatabase from, SyncDatabase to, bool baseline,
        IReadOnlyDictionary<Guid, SyncDecision> decisions, IReadOnlyList<Planned> planned, IReadOnlyList<Guid> trashing, IReadOnlyDictionary<Guid, bool> done,
        PlanRun? run, List<SyncEntry> skipped, List<DroppedValue> dropped, List<LeftOutDraft> drafts, IReadOnlyList<ModelDifference> differences, SyncFiles files,
        int steps, IReadOnlyDictionary<Guid, TargetItem> targets, IReadOnlyDictionary<Guid, ExportCandidate> bySource, CancellationToken cancellationToken)
    {
        // After a real run, new content has its ref, and an item whose steps all found it the same counts as unchanged.
        var created = run?.Created ?? new Dictionary<string, string>();
        var after = run is { DryRun: false } ? await TargetItemsAsync(planned.Select(p => p.Item.Guid), cancellationToken) : new Dictionary<Guid, TargetItem>();
        SyncEntry Of(Guid guid, string? reason = null)
        {
            var reference = after.TryGetValue(guid, out var now) ? ContentIdentity.RefFor(now.Id)
                : targets.TryGetValue(guid, out var there) ? ContentIdentity.RefFor(there.Id)
                : created.GetValueOrDefault(ExportPlanBuilder.PlanId(guid));
            return bySource.TryGetValue(guid, out var candidate)
                ? new SyncEntry(guid, reference, candidate.Type, candidate.Header.LanguageRow(null)?.Name, reason)
                : new SyncEntry(guid, reference, targets.TryGetValue(guid, out var item) ? target.Model.TypeName(item.TypeId) : null, null, reason);
        }
        var real = run is { DryRun: false };
        bool Wrote(Guid guid) => !real || (done.TryGetValue(guid, out var saved) && saved);
        var written = planned.Where(p => Wrote(p.Item.Guid)).ToList();
        var same = planned.Where(p => real && done.TryGetValue(p.Item.Guid, out var saved) && !saved).ToList();

        var result = new
        {
            Created = written.Where(p => p.Decision.Step == SyncStep.Create).Select(p => Of(p.Item.Guid)).ToList(),
            Restored = written.Where(p => p.Decision.Step == SyncStep.Restore).Select(p => Of(p.Item.Guid)).ToList(),
            Updated = written.Where(p => p.Decision.Step == SyncStep.Compare && p.Different).Select(p => Of(p.Item.Guid, p.Decision.Overwrite ? "local changes overwritten (--overwrite-local)" : null)).ToList(),
            Moved = written.Where(p => p.Moved).Select(p => Of(p.Item.Guid, $"to {(p.Candidate.Owner is { } owner ? $"the \"For this page\" folder of {owner:D}" : p.Candidate.Parent?.ToString("D"))}")).ToList(),
            Trashed = trashing.Where(g => !real || done.ContainsKey(g)).Select(g => Of(g)).ToList(),
            Conflicts = decisions.Where(d => d.Value.Step == SyncStep.Conflict).Select(d => Of(d.Key, d.Value.Reason)).ToList(),
            Kept = decisions.Where(d => d.Value.Step == SyncStep.Kept).Select(d => Of(d.Key, d.Value.Reason)).ToList(),
            Gone = decisions.Where(d => d.Value.Step == SyncStep.Gone).Select(d => Of(d.Key, d.Value.Reason)).ToList(),
        };
        var unchanged = decisions.Count(d => d.Value.Step == SyncStep.Unchanged) + same.Count;
        return new SyncResult(
            from,
            to,
            options.DryRun,
            baseline,
            state.Path,
            new SyncCounts(result.Created.Count, result.Updated.Count, result.Moved.Count, result.Restored.Count, result.Trashed.Count, unchanged,
                result.Kept.Count, result.Conflicts.Count, result.Gone.Count, skipped.Count),
            result.Created,
            result.Updated,
            result.Moved,
            result.Restored,
            result.Trashed,
            result.Conflicts,
            result.Kept,
            result.Gone,
            skipped,
            dropped,
            drafts,
            differences,
            files,
            steps,
            KeepPlan ? folder : null,
            run,
            NotSynced);
    }
}
