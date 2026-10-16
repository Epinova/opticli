using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Output;
using OptiCli.Core.Properties;

namespace OptiCli.Core.Export;

/// <summary>Where an export starts: an item, with or without itself and what is below it.</summary>
/// <param name="IncludeSelf">Export the item itself (not for a system folder such as the global assets, whose content is exported).</param>
public sealed record ExportRoot(int Id, bool IncludeSelf, bool Descendants);

/// <summary>An item an export leaves out, and why.</summary>
public sealed record SkippedItem(string Ref, Guid Guid, string? Type, string? Name, string Reason);

/// <summary>A newer draft than the published version an export writes; it isn't written.</summary>
/// <param name="Draft">The draft, as a version ref (<c>123_456</c>).</param>
public sealed record LeftOutDraft(string Ref, Guid Guid, string? Name, string? Language, string Draft);

/// <param name="BlobUris">Each media item's file, as the version written stores it.</param>
public sealed record ExportRead(
    IReadOnlyList<ExportItem> Items,
    IReadOnlyList<SkippedItem> Skipped,
    IReadOnlyList<LeftOutDraft> Drafts,
    IReadOnlyList<DroppedValue> Dropped,
    IReadOnlyDictionary<Guid, string> BlobUris);

/// <summary>
/// An item a read selected, before its values are read: where it is and which version of each branch it writes. A sync
/// decides from these which items it needs to read at all.
/// </summary>
/// <param name="Parent">The parent's GUID; for content in a "For this page" folder, the folder's (see <paramref name="Owner"/>).</param>
/// <param name="Owner">For a block or media item in a "For this page" folder: the content that folder belongs to.</param>
/// <param name="Branches">The branches to write, master first.</param>
public sealed record ExportCandidate(
    ContentHeader Header,
    ContentKind Kind,
    string Type,
    Guid? Parent,
    Guid? Owner,
    int MasterLanguageId,
    IReadOnlyList<CandidateBranch> Branches)
{
    public Guid Guid => Header.Guid;

    public int Id => Header.Id;
}

/// <param name="Language">The branch's language code; null for content without languages.</param>
/// <param name="VersionId">The version written: the published one, or the newest draft of a branch never published.</param>
public sealed record CandidateBranch(int LanguageId, string? Language, int VersionId, bool Published);

/// <summary>The items a read selected (in the source's tree order, parents first) and those it leaves out.</summary>
public sealed record ExportSelection(IReadOnlyList<ExportCandidate> Items, IReadOnlyList<SkippedItem> Skipped);

/// <summary>
/// Reads what an export writes, from the database alone (so it works on a database only read, production's): the items
/// below the roots and the blocks and media in their "For this page" folders, each language branch's version as
/// <c>get</c> shows it. The root, the recycle bin, the asset roots and "For this page" folders themselves are the CMS's
/// own and never exported. Selecting is cheap (headers only); the values are read in batches of
/// <see cref="BatchSize"/> items.
/// </summary>
public sealed class ExportReader(ContentSession session)
{
    /// <summary>The recycle bin's GUID, the same in every CMS database.</summary>
    private static readonly Guid RecycleBin = Guid.Parse("2f40ba47-f4fc-47ae-a244-0b909d4cf988");

    /// <summary>Items whose values are read together: a few queries per batch rather than about ten per branch.</summary>
    public const int BatchSize = 250;

    private CmsDatabase Db => session.Db;

    private CmsModel Model => session.Model;

    /// <param name="languages">The branches to write (language ids); null for every branch. The master branch is always written.</param>
    public async Task<ExportRead> ReadAsync(IReadOnlyList<ExportRoot> roots, IReadOnlySet<int>? languages, CancellationToken cancellationToken)
    {
        var selection = await SelectAsync(roots, languages, cancellationToken);
        var read = await ReadAsync(selection.Items, cancellationToken);
        return read with { Skipped = [.. selection.Skipped, .. read.Skipped] };
    }

    /// <summary>The items below the roots (and in their "For this page" folders), without their values.</summary>
    /// <param name="languages">The branches to write (language ids); null for every branch. The master branch is always written.</param>
    public async Task<ExportSelection> SelectAsync(IReadOnlyList<ExportRoot> roots, IReadOnlySet<int>? languages, CancellationToken cancellationToken) =>
        await CandidatesAsync(await SelectIdsAsync(roots, cancellationToken), languages, cancellationToken);

    /// <summary>
    /// The given items as a read writes them, without their values: those outside the recycle bin that an export can write
    /// (the rest are listed as skipped, or left out silently when they are the CMS's own).
    /// </summary>
    public async Task<ExportSelection> CandidatesAsync(IReadOnlyCollection<int> ids, IReadOnlySet<int>? languages, CancellationToken cancellationToken)
    {
        var headers = await ContentHeaderReader.ByIdsAsync(Db, ids, cancellationToken);
        var structure = await StructureAsync(headers.Values.SelectMany(h => h.AncestorIds.Append(h.Id)).Distinct(), cancellationToken);
        var system = SystemFolders();
        var candidates = new List<ExportCandidate>();
        var skipped = new List<SkippedItem>();

        var ordered = headers.Values
            .Where(h => !h.Deleted && h.ParentId is not null && !system.Contains(h.Id) && h.Guid != RecycleBin && structure.GetValueOrDefault(h.Id).Owner is null)
            .OrderBy(h => h.AncestorIds.Count).ThenBy(h => h.PeerOrder).ThenBy(h => h.Id)
            .ToList();
        foreach (var header in ordered)
        {
            var kind = Model.Kind(header.TypeId);
            var type = Model.TypeName(header.TypeId);
            var name = header.LanguageRow(null)?.Name;
            void Skip(string reason) => skipped.Add(new SkippedItem(ContentIdentity.RefFor(header.Id), header.Guid, type, name, reason));

            if (kind is ContentKind.Other or ContentKind.Contract || Model.Type(header.TypeId) is null)
            {
                Skip($"{type} isn't content an export writes (pages, blocks, media and folders)");
                continue;
            }
            if (header.Blueprint)
            {
                Skip("a Visual Builder blueprint, a template for new content");
                continue;
            }

            // Directly in a "For this page" folder: placed with its owner (a create makes the folder, with a GUID of its own).
            // Deeper, in a folder there, by that folder as any other parent.
            Guid? owner = null;
            var inAssets = header.AncestorIds.Any(a => structure.GetValueOrDefault(a).Owner is not null);
            if (inAssets && kind is not (ContentKind.Media or ContentKind.Block or ContentKind.Section or ContentKind.Element or ContentKind.Folder))
            {
                Skip($"a {kind.Name()} in a \"For this page\" folder, where only blocks, media and folders go");
                continue;
            }
            if (header.ParentId is { } parentId && structure.GetValueOrDefault(parentId).Owner is { } folderOwner)
            {
                owner = folderOwner;
            }

            var branches = new List<CandidateBranch>();
            var master = header.Languages.ContainsKey(header.MasterLanguageId) ? header.MasterLanguageId : header.Languages.Keys.DefaultIfEmpty(0).Min();
            foreach (var (languageId, row) in header.Languages
                .OrderBy(l => l.Key == master ? 0 : 1).ThenBy(l => Model.Language(l.Key)?.Code, StringComparer.OrdinalIgnoreCase))
            {
                if (languageId != master && languages is not null && !languages.Contains(languageId))
                {
                    continue;
                }
                if (row.VersionId is { } versionId)
                {
                    branches.Add(new CandidateBranch(languageId, Language(languageId), versionId, row.Status == VersionStatus.Published));
                }
            }
            if (branches.Count == 0)
            {
                Skip("it has no language branch with a version");
                continue;
            }
            candidates.Add(new ExportCandidate(
                header,
                kind,
                type,
                header.ParentId is { } parent ? structure.GetValueOrDefault(parent).Guid : null,
                owner,
                master,
                branches));
        }
        return new ExportSelection(candidates, skipped);
    }

    /// <summary>The values of the candidates' branches, as <c>get --full --all-properties</c> shows each version written.</summary>
    /// <param name="progress">Called after each batch with the items read so far and the total.</param>
    public async Task<ExportRead> ReadAsync(IReadOnlyList<ExportCandidate> candidates, CancellationToken cancellationToken, Action<int, int>? progress = null)
    {
        var removed = await RemovedPropertiesAsync(cancellationToken);
        var items = new List<ExportItem>();
        var drafts = new List<LeftOutDraft>();
        var dropped = new List<DroppedValue>();
        var blobs = new Dictionary<Guid, string>();
        var loader = new ContentLoader(Db, session.Identities);
        var decode = new DecodeOptions(Full: true, AllProperties: true, Composition: true);

        var done = 0;
        foreach (var batch in candidates.Chunk(BatchSize))
        {
            var documents = await loader.GetVersionsAsync(batch.SelectMany(c => c.Branches.Select(b => (c.Id, b.VersionId))).ToList(), decode, cancellationToken);
            var versions = await VersionReader.ByIdsAsync(Db, Model, batch.Where(c => c.Kind == ContentKind.Media).Select(c => c.Branches[0].VersionId), cancellationToken);
            var unpublished = await ContentHeaderReader.NewestUnpublishedAsync(Db, batch.Where(c => c.Branches.Any(b => b.Published)).Select(c => c.Id), cancellationToken);
            foreach (var candidate in batch)
            {
                var header = candidate.Header;
                var branches = new List<ExportBranch>();
                foreach (var branch in candidate.Branches)
                {
                    if (!documents.TryGetValue(branch.VersionId, out var read))
                    {
                        continue;
                    }
                    var document = (JsonObject)JsonOutput.ToNode(read)!;
                    if (document["properties"] is JsonObject properties)
                    {
                        foreach (var property in Model.PropertiesOf(header.TypeId).Where(p => removed.Contains(p.Id) && properties.ContainsKey(p.Name)))
                        {
                            if (properties[property.Name]?["value"] is not null)
                            {
                                dropped.Add(new DroppedValue(header.Guid, branch.Language, property.Name, "the source site's code no longer has this property (removed code left it in the database)"));
                            }
                            properties.Remove(property.Name);
                        }
                    }
                    if (branch.Published && unpublished.TryGetValue((header.Id, branch.LanguageId), out var draft) && draft > branch.VersionId)
                    {
                        drafts.Add(new LeftOutDraft(ContentIdentity.RefFor(header.Id), header.Guid, header.Languages.GetValueOrDefault(branch.LanguageId)?.Name, branch.Language, ContentIdentity.RefFor(header.Id, draft)));
                    }
                    if (candidate.Kind == ContentKind.Media && branch.LanguageId == candidate.MasterLanguageId && versions.GetValueOrDefault(branch.VersionId)?.BlobUri is { } blob)
                    {
                        blobs[header.Guid] = blob;
                    }
                    branches.Add(new ExportBranch(branch.Language, branch.VersionId, branch.Published, document));
                }
                if (branches.Count == 0)
                {
                    continue;
                }

                var cultureSpecific = Model.PropertiesOf(header.TypeId).Where(p => p.CultureSpecific).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (Compositions.LayoutProperty(Model, header.TypeId) is { CultureSpecific: true })
                {
                    cultureSpecific.Add(Compositions.Field);
                }
                items.Add(new ExportItem(
                    header.Id,
                    header.Guid,
                    candidate.Kind,
                    candidate.Type,
                    candidate.Parent,
                    candidate.Owner,
                    Language(candidate.MasterLanguageId),
                    branches,
                    cultureSpecific,
                    items.Count));
            }
            done += batch.Length;
            progress?.Invoke(done, candidates.Count);
        }
        return new ExportRead(items, [], drafts, dropped, blobs);
    }

    /// <summary>The language's code; null for content without languages.</summary>
    private string? Language(int languageId) => Model.Language(languageId) is { IsInvariant: false } language ? language.DisplayCode : null;

    /// <summary>
    /// The roots and what is below them, and the content of the "For this page" folders of all of it (and of their content),
    /// outside the recycle bin.
    /// </summary>
    private async Task<IReadOnlyList<int>> SelectIdsAsync(IReadOnlyList<ExportRoot> roots, CancellationToken cancellationToken)
    {
        var selected = new HashSet<int>(roots.Where(r => r.IncludeSelf).Select(r => r.Id));
        selected.UnionWith(await DescendantsAsync(roots.Where(r => r.Descendants).Select(r => r.Id).ToList(), cancellationToken));
        var visited = new HashSet<int>();
        while (selected.Except(visited).ToList() is { Count: > 0 } next)
        {
            visited.UnionWith(next);
            var folders = new List<int>();
            foreach (var list in SqlLists.Ints(next))
            {
                folders.AddRange(await Db.QueryAsync(
                    $"SELECT f.pkID FROM tblContent o JOIN tblContent f ON f.ContentGUID = o.ContentAssetsID WHERE o.pkID IN ({list}) AND f.Deleted = 0",
                    r => r.GetInt32("pkID"), cancellationToken));
            }
            selected.UnionWith(folders);
            selected.UnionWith(await DescendantsAsync(folders, cancellationToken));
        }
        return selected.ToList();
    }

    private async Task<IReadOnlyList<int>> DescendantsAsync(IReadOnlyList<int> roots, CancellationToken cancellationToken)
    {
        var result = new List<int>();
        foreach (var list in SqlLists.Ints(roots))
        {
            result.AddRange(await Db.QueryAsync(
                $"SELECT t.fkChildID FROM tblTree t JOIN tblContent c ON c.pkID = t.fkChildID WHERE t.fkParentID IN ({list}) AND c.Deleted = 0",
                r => r.GetInt32("fkChildID"), cancellationToken));
        }
        return result;
    }

    /// <summary>Each item's GUID, and the content its "For this page" folder belongs to when it is one (<c>ContentOwnerID</c>).</summary>
    private async Task<IReadOnlyDictionary<int, (Guid Guid, Guid? Owner)>> StructureAsync(IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, (Guid, Guid?)>();
        foreach (var list in SqlLists.Ints(ids))
        {
            var rows = await Db.QueryAsync($"SELECT pkID, ContentGUID, ContentOwnerID FROM tblContent WHERE pkID IN ({list})",
                r => (Id: r.GetInt32("pkID"), Guid: r.GetGuid("ContentGUID"), Owner: r.GetGuidOrNull("ContentOwnerID")), cancellationToken);
            foreach (var row in rows)
            {
                result[row.Id] = (row.Guid, row.Owner is { } owner && owner != Guid.Empty ? owner : null);
            }
        }
        return result;
    }

    /// <summary>
    /// Property definitions the source site's code no longer has (<c>ExistsOnModel = 0</c>): values removed code left
    /// behind, which no editor sees, and which the target's code doesn't have either.
    /// </summary>
    private async Task<IReadOnlySet<int>> RemovedPropertiesAsync(CancellationToken cancellationToken) =>
        (await Db.QueryAsync("SELECT pkID FROM tblPropertyDefinition WHERE ExistsOnModel = 0", r => r.GetInt32("pkID"), cancellationToken)).ToHashSet();

    /// <summary>The CMS's own folders: the asset roots (global, "For this page", each site's).</summary>
    private HashSet<int> SystemFolders()
    {
        var folders = new HashSet<int>();
        foreach (var id in new[] { Model.Sites.GlobalAssetsRoot, Model.Sites.ContentAssetsRoot }.Concat(Model.Sites.All.Select(Urls.SiteMap.AssetsRootId)))
        {
            if (id is { } folder)
            {
                folders.Add(folder);
            }
        }
        return folders;
    }

    /// <summary>
    /// The roots of <c>--site</c>: the site's start page and everything below it, and the content of its assets folder and
    /// the global assets folder (not the folders themselves).
    /// </summary>
    public IReadOnlyList<ExportRoot> SiteRoots(SiteInfo site)
    {
        var roots = new List<ExportRoot>();
        if (Urls.SiteMap.StartPageId(site) is { } start)
        {
            roots.Add(new ExportRoot(start, IncludeSelf: true, Descendants: true));
        }
        foreach (var assets in new[] { Urls.SiteMap.AssetsRootId(site), Model.Sites.GlobalAssetsRoot }.OfType<int>().Distinct())
        {
            roots.Add(new ExportRoot(assets, IncludeSelf: false, Descendants: true));
        }
        return roots;
    }

    /// <summary>A ref's root: the item, or for one of the CMS's own folders (the asset roots) only what is below it.</summary>
    public ExportRoot Root(int id, bool descendants) =>
        SystemFolders().Contains(id) ? new ExportRoot(id, IncludeSelf: false, Descendants: true) : new ExportRoot(id, IncludeSelf: true, descendants);
}
