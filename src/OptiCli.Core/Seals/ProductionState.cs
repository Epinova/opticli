using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Sync;

namespace OptiCli.Core.Seals;

/// <summary>One language branch of touched content, as production had it.</summary>
/// <param name="Language">The branch's code; <c>*</c> for content without languages.</param>
/// <param name="Latest">The newest version, with when it was saved: an id alone comes back for another version once a database is restored from a backup.</param>
/// <param name="Published">The published version; null when the branch isn't published.</param>
public sealed record SealedBranch(string Language, int Latest, DateTime? LatestSaved, int? Published);

/// <summary>Content the plan touches or names, as production had it.</summary>
/// <param name="Exists">Production has content with the GUID (in the recycle bin or not).</param>
/// <param name="Parent">Its parent's GUID: a move makes no version, so the place is recorded too.</param>
/// <param name="Branches">For content the plan writes; empty for content it only names (a parent, a move's target).</param>
public sealed record SealedItem(Guid Guid, bool Exists, bool Deleted, Guid? Parent, string? Type, string? Name, IReadOnlyList<SealedBranch> Branches);

/// <summary>
/// What a seal checks in production: its content model (as a fingerprint) and every item the plan touches or names. The
/// sealed run reads it again, before it asks the user and right before the first save, and refuses when anything changed.
/// </summary>
/// <param name="Model">SHA-256 of production's content types and properties (<see cref="ModelFingerprint"/>).</param>
public sealed record ProductionState(string Model, IReadOnlyList<SealedItem> Items)
{
    /// <param name="touched">Content the plan writes (its versions are recorded).</param>
    /// <param name="named">Other content the plan names (only whether it is there, and where).</param>
    public static async Task<ProductionState> ReadAsync(ContentSession session, IReadOnlyCollection<Guid> touched, IReadOnlyCollection<Guid> named, CancellationToken cancellationToken)
    {
        var model = ModelFingerprint(session.Model, await ModelCompatibility.RemovedAsync(session.Db, cancellationToken));
        var all = touched.Concat(named).Distinct().ToList();
        var ids = await ContentHeaderReader.IdsByGuidsAsync(session.Db, all, cancellationToken);
        var headers = await ContentHeaderReader.ByIdsAsync(session.Db, ids.Values, cancellationToken);
        var touchedIds = touched.Where(ids.ContainsKey).Select(g => ids[g]).ToList();
        var latest = await VersionReader.LatestStampsAsync(session.Db, session.Model, touchedIds, cancellationToken);
        var parents = await ContentHeaderReader.ByIdsAsync(session.Db, headers.Values.Select(h => h.ParentId).OfType<int>().Distinct(), cancellationToken);
        var items = new List<SealedItem>();
        foreach (var guid in all.OrderBy(g => g))
        {
            if (!ids.TryGetValue(guid, out var id) || !headers.TryGetValue(id, out var header))
            {
                items.Add(new SealedItem(guid, false, false, null, null, null, []));
                continue;
            }
            var branches = touched.Contains(guid)
                ? header.Languages.Values
                    .Select(l => latest.TryGetValue((id, l.LanguageId), out var newest)
                        ? new SealedBranch(LanguageCode(session.Model, l.LanguageId), newest.Id, newest.Saved, l.Status == VersionStatus.Published ? l.VersionId : null)
                        : null)
                    .OfType<SealedBranch>()
                    .OrderBy(b => b.Language, StringComparer.Ordinal)
                    .ToList()
                : [];
            items.Add(new SealedItem(guid, true, header.Deleted,
                header.ParentId is { } parent && parents.TryGetValue(parent, out var parentHeader) ? parentHeader.Guid : null,
                session.Model.TypeName(header.TypeId), header.LanguageRow(null)?.Name, branches));
        }
        return new ProductionState(model, items);
    }

    /// <summary>What differs in <paramref name="now"/> from what was sealed, one line each; empty when nothing does.</summary>
    public static IReadOnlyList<string> Changes(ProductionState sealedState, ProductionState now)
    {
        var changes = new List<string>();
        if (!string.Equals(sealedState.Model, now.Model, StringComparison.Ordinal))
        {
            changes.Add($"the content model changed (fingerprint {Short(sealedState.Model)}, now {Short(now.Model)}): a deployment, or a change in admin mode");
        }
        var current = now.Items.ToDictionary(i => i.Guid);
        foreach (var before in sealedState.Items)
        {
            var shown = before.Name ?? current.GetValueOrDefault(before.Guid)?.Name;
            var name = shown is null ? before.Guid.ToString("D") : $"{before.Guid:D} ('{shown}')";
            if (!current.TryGetValue(before.Guid, out var after))
            {
                changes.Add($"{name} wasn't read again");
                continue;
            }
            if (before.Exists != after.Exists)
            {
                changes.Add(after.Exists ? $"{name} was created since" : $"{name} was deleted for good since");
                continue;
            }
            if (before.Deleted != after.Deleted)
            {
                changes.Add(after.Deleted ? $"{name} was moved to the recycle bin since" : $"{name} was restored from the recycle bin since");
            }
            if (before.Parent != after.Parent)
            {
                changes.Add($"{name} was moved since (parent {before.Parent:D}, now {after.Parent:D})");
            }
            var branches = after.Branches.ToDictionary(b => b.Language, StringComparer.Ordinal);
            foreach (var branch in before.Branches)
            {
                if (!branches.Remove(branch.Language, out var newer))
                {
                    changes.Add($"{name}: its {branch.Language} branch is gone");
                    continue;
                }
                if (newer.Latest != branch.Latest || newer.LatestSaved != branch.LatestSaved)
                {
                    changes.Add($"{name}: its {branch.Language} branch has a newer version ({branch.Latest}, now {newer.Latest} saved {newer.LatestSaved?.ToString("u", CultureInfo.InvariantCulture) ?? "at an unknown time"})");
                }
                else if (newer.Published != branch.Published)
                {
                    changes.Add($"{name}: its {branch.Language} branch was {(newer.Published is null ? "unpublished" : "published")} since");
                }
            }
            foreach (var added in branches.Keys)
            {
                changes.Add($"{name}: has a new {added} branch");
            }
        }
        return changes;
    }

    /// <summary>
    /// The content model as a SHA-256 (hex): each type's GUID, name and kind, and each property's name, type (as the sync
    /// compares them: <see cref="ModelCompatibility"/>) and whether it is per language, without properties the code no
    /// longer has. Ids are left out: they differ even between copies of one database. So is the schema version: a login
    /// that only reads can't run <c>sp_DatabaseVersion</c>, and the run's site checks the schema itself before it starts.
    /// </summary>
    public static string ModelFingerprint(CmsModel model, IReadOnlySet<int> removed)
    {
        var lines = new List<string>();
        foreach (var type in model.Types)
        {
            lines.Add($"type {type.Guid:D} {type.Name} {type.Kind}");
            lines.AddRange(model.PropertiesOf(type.Id).Where(p => !removed.Contains(p.Id))
                .Select(p => $"property {type.Guid:D} {p.Name.ToLowerInvariant()} {ModelCompatibility.Kind(model, p)} {(p.CultureSpecific ? "culture" : "shared")}"));
        }
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
    }

    public static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private static string LanguageCode(CmsModel model, int languageId) =>
        model.Language(languageId) is { IsInvariant: false } language ? language.Code : "*";
}
