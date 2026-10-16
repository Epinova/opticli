using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Export;
using OptiCli.Core.Output;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Sync;

/// <param name="Plan">The plan file written (<c>plan.json</c> in the folder).</param>
/// <param name="Media">Media in the selection, left out: a sealed apply writes no files.</param>
/// <param name="Drafts">Local branches with a newer draft than the published version the plan writes.</param>
/// <param name="Unchanged">Items production already has as the local database has them (and where it has them).</param>
/// <param name="Conflicts">Items production changed since the last pull, or that were never pulled from it: left out, so a push never writes over production's changes.</param>
public sealed record PushPlan(
    string Folder,
    string Plan,
    int Steps,
    IReadOnlyList<SyncEntry> Created,
    IReadOnlyList<SyncEntry> Updated,
    IReadOnlyList<SyncEntry> Moved,
    IReadOnlyList<SyncEntry> Restored,
    int Unchanged,
    IReadOnlyList<SyncEntry> Conflicts,
    IReadOnlyList<SyncEntry> Skipped,
    IReadOnlyList<SyncEntry> Media,
    IReadOnlyList<DroppedValue> Dropped,
    IReadOnlyList<LeftOutDraft> Drafts);

/// <summary>
/// <c>sync push</c>: the plan that brings the local database's content into production, written as a folder for a seal. It is
/// an export of the local database, compared with production over SQL by GUID (read only), with only what differs: content
/// production lacks is created (or restored from its recycle bin), content that differs updated, content moved locally moved.
/// Never anything that deletes, and no media: the sealed run writes no files, so media is left out and listed. Content
/// production has with someone's unpublished changes is left out too, as publishing over it would put those live (the run
/// would refuse it anyway). Content only in production is left alone.
/// <para>
/// A push never writes over production's own changes: content production has is only written when the last pull from it
/// (<paramref name="pulled"/>, the pull's sync state) read the version production has now, in the place it is now.
/// Content production changed since, or that was never pulled from it, is a conflict (left out and listed): pull first.
/// </para>
/// </summary>
/// <param name="pulled">The sync state of pulls from production into this local database; push only reads it.</param>
/// <param name="folder">Where the plan is written (a new or empty folder).</param>
public sealed class SyncPush(ContentSession local, ContentSession production, SyncState pulled, string folder)
{
    /// <summary>Why production's side of an item can't be written over; null when the last pull read it as it is now.</summary>
    public static string? ProductionChanged(ExportCandidate there, SyncedItem? synced)
    {
        if (synced is null)
        {
            return "production has it, and it was never pulled from there (no sync state): pull first (`opticli sync pull --from <name>`), so a push can tell local changes from production's";
        }
        if (SyncDiff.Place(there.Parent, there.Owner) != synced.Source)
        {
            return "moved in production since the last pull: pull first";
        }
        foreach (var branch in there.Branches)
        {
            var language = branch.Language ?? "*";
            var read = synced.Branches.FirstOrDefault(b => string.Equals(b.Key, language, StringComparison.OrdinalIgnoreCase)).Value;
            if (read is null || read.Source != branch.VersionId || read.Published != branch.Published)
            {
                return $"its {language} branch changed in production since the last pull: pull first";
            }
        }
        return null;
    }

    public async Task<PushPlan> BuildAsync(IReadOnlyList<ExportRoot> roots, IReadOnlySet<int>? languages, SyncDatabase from, CancellationToken cancellationToken)
    {
        var readAt = DateTime.UtcNow;
        var selection = await new ExportReader(local).SelectAsync(roots, languages, cancellationToken);
        var skipped = selection.Skipped.Select(s => new SyncEntry(s.Guid, null, s.Type, s.Name, s.Reason)).ToList();
        var media = selection.Items.Where(c => c.Kind == ContentKind.Media)
            .Select(c => Entry(c, "media: a sealed apply writes no files (upload it in production's edit UI)")).ToList();
        var candidates = selection.Items.Where(c => c.Kind != ContentKind.Media).ToList();
        var bySource = candidates.ToDictionary(c => c.Guid);

        // Production's side: what it has (in its recycle bin too) and, for what it has outside it, the values.
        var ids = await ContentHeaderReader.IdsByGuidsAsync(production.Db, candidates.Select(c => c.Guid), cancellationToken);
        var headers = (await ContentHeaderReader.ByIdsAsync(production.Db, ids.Values, cancellationToken)).Values.ToDictionary(h => h.Guid);
        var prodReader = new ExportReader(production);
        var prodSelection = await prodReader.CandidatesAsync(headers.Values.Where(h => !h.Deleted).Select(h => h.Id).ToList(), null, cancellationToken);
        var prodCandidates = prodSelection.Items.ToDictionary(c => c.Guid);
        var theirs = (await prodReader.ReadAsync(prodSelection.Items, cancellationToken)).Items.ToDictionary(i => i.Guid);
        var unpublished = await ContentHeaderReader.NewestUnpublishedAsync(production.Db, prodSelection.Items.Select(c => c.Id), cancellationToken);

        var read = await new ExportReader(local).ReadAsync(candidates, cancellationToken);
        var items = read.Items.ToDictionary(i => i.Guid);
        var dropped = read.Dropped.ToList();

        // Where things go: what production has outside its recycle bin, and what the plan creates or restores there.
        var known = (await ContentHeaderReader.ByIdsAsync(production.Db,
            (await ContentHeaderReader.IdsByGuidsAsync(production.Db, items.Values.SelectMany(i => new[] { i.Parent, i.Owner }.OfType<Guid>()).Concat(ExportPlanBuilder.Referenced(items.Values)).Distinct(), cancellationToken)).Values,
            cancellationToken)).Values.ToDictionary(h => h.Guid);
        var creating = items.Keys.Where(g => !headers.TryGetValue(g, out var header) || header.Deleted).ToHashSet();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var guid in creating.ToList())
            {
                var candidate = bySource[guid];
                var place = candidate.Owner ?? candidate.Parent;
                if (place is { } at && !creating.Contains(at) && !(known.TryGetValue(at, out var there) && !there.Deleted))
                {
                    creating.Remove(guid);
                    items.Remove(guid);
                    skipped.Add(Entry(candidate, $"its {(candidate.Owner is null ? "parent" : "owner")} {at:D} isn't in production, and the push doesn't bring it"));
                    changed = true;
                }
            }
        }
        var target = new ExportTarget(
            g => creating.Contains(g) || known.ContainsKey(g) || headers.ContainsKey(g),
            id => !Guid.TryParse(id, out var group) || production.Model.VisitorGroups.ContainsKey(group),
            name => production.Model.CategoryNames.Contains(name, StringComparer.OrdinalIgnoreCase));

        var planned = new List<(ExportItem Item, string Action)>();
        var moves = new List<ExportCandidate>();
        var conflicts = new List<SyncEntry>();
        var unchanged = 0;
        foreach (var guid in items.Keys.ToList())
        {
            var candidate = bySource[guid];
            var item = items[guid];
            if (creating.Contains(guid))
            {
                planned.Add((item, headers.ContainsKey(guid) ? "restored" : "created"));
                continue;
            }
            if (!prodCandidates.TryGetValue(guid, out var prodCandidate) || !theirs.TryGetValue(guid, out var prodItem))
            {
                skipped.Add(Entry(candidate, "production has it, but not as content an export reads (a system folder, or another kind)"));
                continue;
            }
            if (prodItem.Type != item.Type && production.Model.Type(prodCandidate.Header.TypeId)?.Guid != local.Model.Type(candidate.Header.TypeId)?.Guid)
            {
                skipped.Add(Entry(candidate, $"it is a {prodItem.Type} in production, a {item.Type} locally; a push doesn't change the type of content"));
                continue;
            }
            if (prodCandidate.Branches.FirstOrDefault(b => unpublished.TryGetValue((prodCandidate.Id, b.LanguageId), out var draft) && draft > b.VersionId) is { } pending)
            {
                skipped.Add(Entry(candidate, $"production has unpublished changes to its {pending.Language ?? "*"} branch (someone's draft): publish or discard them there first"));
                continue;
            }
            var moved = SyncDiff.Place(candidate.Parent, candidate.Owner) != SyncDiff.Place(prodCandidate.Parent, prodCandidate.Owner);
            var different = SyncDiff.Same(ExportPlanBuilder.Comparable(item, target), ExportPlanBuilder.Comparable(prodItem)) is not null;
            if (!moved && !different)
            {
                unchanged++;
                continue;
            }
            if (ProductionChanged(prodCandidate, pulled.Items.GetValueOrDefault(guid)) is { } changedThere)
            {
                conflicts.Add(Entry(candidate, changedThere));
                continue;
            }
            if (moved && candidate.Owner is not null)
            {
                skipped.Add(Entry(candidate, "moved into a \"For this page\" folder locally: a push doesn't move content there (move it in production's edit UI)"));
                continue;
            }
            if (moved && candidate.Parent is { } parent && !creating.Contains(parent) && !(known.TryGetValue(parent, out var there) && !there.Deleted))
            {
                skipped.Add(Entry(candidate, $"moved locally under {parent:D}, which production doesn't have"));
                continue;
            }
            if (moved)
            {
                moves.Add(candidate);
            }
            planned.Add((item, different ? "updated" : "moved"));
        }

        var built = ExportPlanBuilder.Build(planned.Select(p => p.Item).ToList(), target: target, now: readAt);
        dropped.AddRange(built.Dropped);
        foreach (var (guid, reason) in built.Skipped)
        {
            planned.RemoveAll(p => p.Item.Guid == guid);
            moves.RemoveAll(m => m.Guid == guid);
            skipped.Add(Entry(bySource[guid], reason));
        }
        var operations = built.Operations.Select(o => o!.DeepClone().AsObject()).ToList();
        var plannedIds = planned.Select(p => p.Item.Guid).ToHashSet();
        foreach (var move in moves)
        {
            var at = operations.FindIndex(o => (string?)o["guid"] == move.Guid.ToString("D"));
            var parent = move.Parent!.Value;
            operations.Insert(at, new JsonObject
            {
                ["op"] = "move",
                ["ref"] = move.Guid.ToString("D"),
                ["to"] = plannedIds.Contains(parent) && creating.Contains(parent) ? $"${ExportPlanBuilder.PlanId(parent)}" : parent.ToString("D"),
            });
        }

        var plan = await WriteAsync(operations, built.Texts, from, readAt, cancellationToken);
        List<SyncEntry> Of(string action) => planned.Where(p => p.Action == action || (action == "moved" && moves.Any(m => m.Guid == p.Item.Guid)))
            .Select(p => Entry(bySource[p.Item.Guid], action == "moved" ? $"to {bySource[p.Item.Guid].Parent:D}" : null)).ToList();
        return new PushPlan(folder, plan, operations.Count, Of("created"), Of("updated"), Of("moved"), Of("restored"), unchanged, conflicts, skipped, media, dropped, read.Drafts.ToList());
    }

    private static SyncEntry Entry(ExportCandidate candidate, string? reason) =>
        new(candidate.Guid, ContentIdentity.RefFor(candidate.Id), candidate.Type, candidate.Header.LanguageRow(null)?.Name, reason);

    private async Task<string> WriteAsync(List<JsonObject> operations, IReadOnlyDictionary<string, string> texts, SyncDatabase from, DateTime readAt, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
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
                ["push"] = true,
            },
            ["operations"] = new JsonArray(operations.Select(o => (JsonNode)o).ToArray()),
        };
        var plan = Path.Combine(folder, "plan.json");
        await File.WriteAllTextAsync(plan, document.ToJsonString(new JsonSerializerOptions(JsonOutput.Options) { WriteIndented = true }) + "\n", new UTF8Encoding(false), cancellationToken);
        return plan;
    }
}
