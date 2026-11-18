using System.Globalization;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using OptiCli.Cms.Compat;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// New blocks in ContentAreas made as the site's edit UI makes them when it doesn't make inline blocks
/// (<c>UIOptions.InlineBlocksInContentAreaEnabled</c> off, CMS 12's default): shared blocks in the "For this page"
/// folder of the content being written (its owner), as "Create a new block" does. opticli creates only content an editor
/// could have made on the same site, and such a site may not render inline blocks at all.
/// </summary>
/// <remarks>
/// <para>The writer (<see cref="PropertyWriter"/>) builds each new block's values as it builds an inline block's, and
/// records it here as <see cref="Pending"/>; its area item carries the block's GUID until the block is saved (a GUID
/// survives the copies area edits make of an area, a reference to the item wouldn't). <see cref="WriteFlow"/> then checks
/// them with the content (<see cref="Prepare"/>), saves them before the content (<see cref="Save"/>), and after a publish
/// publishes their drafts along (<see cref="PublishAlong"/>).</para>
/// <para>The owner is always the content being written, also for a block inside an inline block's area or inside another
/// new block, as the CMS 13 edit UI does for everything edited from the page (phase 0 of the plan). Blocks are made in
/// the language being written, which becomes their master language. The folder and the blocks are saved without the
/// CMS's access check (<see cref="CmsCall.SaveAsset"/>), as the CMS saves the folder itself: the editor's rights are
/// checked here first, as the edit UI checks them before it offers "Create a new block".</para>
/// <para>Blocks are saved with the owner's save: a draft (into the write's project, if it has one), never published at
/// creation, unlike the edit UI, which publishes a new block at once when the editor may. A publish of the owner through
/// opticli publishes the drafts of the folder's blocks its areas show, and only those.</para>
/// </remarks>
internal sealed class SharedAreaBlocks(CmsCall call, ContentLocator locator)
{
    /// <summary>A shared block a write makes (or, taking over a block of the folder, changes) for an area item.</summary>
    internal sealed class Pending
    {
        /// <summary>
        /// The area item, for messages and the result: <c>MainArea[2]</c>. Where it was asked for until
        /// <see cref="Prepare"/> finds where it is once every change of the write is made (a later add or move shifts it).
        /// </summary>
        public required string Path { get; set; }

        public required ContentType Type { get; init; }

        /// <summary>The values, built as an inline block's are (type defaults, then what the item gives).</summary>
        public required BlockData Values { get; init; }

        /// <summary>The name the item gives; null to generate one (a new block) or keep the block's (an update).</summary>
        public string? GivenName { get; set; }

        /// <summary>A new block's GUID, which its area item carries (<see cref="ContentAreaItem.ContentGuid"/>) until it is saved.</summary>
        public Guid Guid { get; init; }

        /// <summary>For a taken-over block: its latest version in the language being written.</summary>
        public IContent? Existing { get; init; }

        /// <summary>The block as it is saved, made by <see cref="Prepare"/>.</summary>
        public IContent? Content { get; set; }

        public string Name { get; set; } = "";

        public bool IsNew => Existing is null;
    }

    private readonly List<Pending> _pending = [];

    private IContent? _folder;

    private bool _folderRead;

    /// <summary>The content being written, whose "For this page" folder the blocks go in.</summary>
    public IContent? Owner { get; set; }

    public IReadOnlyList<Pending> All => _pending;

    public int Count => _pending.Count;

    public void Add(Pending pending) => _pending.Add(pending);

    /// <summary>Forgets the blocks recorded after the first <paramref name="count"/> (an add that was there already).</summary>
    public void Truncate(int count) => _pending.RemoveRange(count, _pending.Count - count);

    /// <summary>
    /// An item the write took out again (an area remove after an add): the new block it shows, and those inside that
    /// block, aren't made.
    /// </summary>
    public void Forget(ContentAreaItem item)
    {
        if (Of(item) is not { } pending)
        {
            return;
        }
        foreach (var inner in Reachable(pending.Values).ToList())
        {
            Forget(inner);
        }
        _pending.Remove(pending);
    }

    /// <summary>The language being written; null for content that isn't localizable.</summary>
    public CultureInfo? Language => Owner is ILocalizable { Language: { } language } && !CultureInfo.InvariantCulture.Equals(language) ? language : null;

    /// <summary>The new block an item shows, by the GUID it carries until the block is saved; null for any other item.</summary>
    public Pending? Of(ContentAreaItem item) =>
        item.ContentGuid != Guid.Empty && ContentReference.IsNullOrEmpty(item.ContentLink) && InlineBlocks.Of(item) is null
            ? _pending.FirstOrDefault(p => p.IsNew && p.Guid == item.ContentGuid)
            : null;

    /// <summary>The owner's "For this page" folder; null when it has none yet (new content has none).</summary>
    public IContent? Folder()
    {
        if (!_folderRead)
        {
            _folderRead = true;
            _folder = Owner is null || ContentReference.IsNullOrEmpty(Owner.ContentLink)
                ? null
                : call.Service<ContentAssetHelper>().GetAssetFolder(Owner.ContentLink.ToReferenceWithoutVersion());
        }
        return _folder;
    }

    /// <summary>
    /// The block <paramref name="item"/> shows, when it is a shared block in the owner's own "For this page" folder (of
    /// the content type <paramref name="typeId"/>, when given) that the caller can read: its latest version in the language
    /// being written. Null for anything else, also a block of the folder without a branch in that language.
    /// </summary>
    public IContent? OwnBlock(ContentAreaItem item, int? typeId = null)
    {
        if (ContentReference.IsNullOrEmpty(item.ContentLink) || InlineBlocks.Of(item) is not null || Folder() is not { } folder)
        {
            return null;
        }
        if (!locator.Repository.TryGet<IContent>(item.ContentLink.ToReferenceWithoutVersion(), new LoaderOptions { LanguageLoaderOption.FallbackWithMaster() }, out var content)
            || content is not BlockData
            || !content.ParentLink.CompareToIgnoreWorkID(folder.ContentLink)
            || (typeId is { } id && content.ContentTypeID != id)
            || !call.CanRead(content))
        {
            return null;
        }
        var branch = locator.Versions(content.ContentLink, Language);
        return branch.Count == 0 ? null : locator.Repository.Get<IContent>(branch[0].ContentLink);
    }

    /// <summary>
    /// Before anything is saved (a dry run's too): the caller's rights, each block's name and content as it will be saved,
    /// and its checks, as issues named by its area item (<c>MainArea[2].Heading</c>), so the content and its blocks are
    /// valid together or nothing is saved.
    /// </summary>
    /// <remarks>
    /// The rights the edit UI asks before it offers "Create a new block": Create on the content (on new content, on its
    /// parent), and Edit, as the content changes; the type as the edit UI offers it for the folder
    /// (<c>ListAvailable(owner, contentFolder: true)</c>, as its <c>ContentTypeStore</c> asks) and as the folder takes it
    /// (<see cref="Operations.CreateOperation.Placement"/>). The developer passes the access checks, as for any save.
    /// Values are validated as for a publish: the edit UI's form requires required values before it saves.
    /// </remarks>
    /// <exception cref="AgentException">
    /// <c>usage</c> for content that can't have a "For this page" folder; <c>refused</c> without the access; the project's
    /// refusals (<see cref="ProjectSave.Check"/>).
    /// </exception>
    public List<ValidationIssue> Prepare(IContent owner, ProjectSave? project)
    {
        if (_pending.Count == 0)
        {
            return [];
        }
        Owner = owner;
        Place(owner);
        if (_pending.Count == 0)
        {
            return [];
        }
        if (owner is not IResourceable)
        {
            throw AgentException.Usage($"{What(owner)} can't have a \"For this page\" folder, so the new blocks in its ContentAreas ({string.Join(", ", _pending.Select(p => p.Path))}) can't be made there, as the site's edit UI makes them.",
                "Add an existing shared block by its ref instead. Nothing was saved.");
        }
        var isNew = ContentReference.IsNullOrEmpty(owner.ContentLink);
        if (isNew)
        {
            call.RequireAccess(locator.LoadUnchecked(owner.ParentLink), AccessLevel.Create);
        }
        else
        {
            call.RequireAccess(owner, AccessLevel.Edit);
            call.RequireAccess(owner, AccessLevel.Create);
        }

        var folder = Folder();
        var types = call.Service<IContentTypeRepository>();
        var folderType = types.Load(typeof(ContentAssetFolder));
        var parent = folder?.ContentLink ?? AssetsRoot(owner);
        var names = new HashSet<string>(folder is null ? [] : locator.Repository.GetChildren<IContent>(folder.ContentLink, new LoaderOptions { LanguageLoaderOption.FallbackWithMaster() }).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var offered = call.Properties.Checks && !isNew
            ? call.Service<ContentTypeAvailabilityService>().ListAvailable(owner, true, call.Service<IPrincipalAccessor>().Principal).Select(t => t.ID).ToHashSet()
            : null;
        var issues = new List<ValidationIssue>();
        foreach (var pending in _pending)
        {
            if (pending.IsNew)
            {
                pending.Name = string.IsNullOrWhiteSpace(pending.GivenName) ? Generated(owner, pending.Type, names) : pending.GivenName.Trim();
                names.Add(pending.Name);
                var content = (Language is { } language
                    ? locator.Repository.GetDefault<IContent>(parent, pending.Type.ID, language)
                    : locator.Repository.GetDefault<IContent>(parent, pending.Type.ID))
                    ?? throw new InvalidOperationException($"The CMS made no {pending.Type.Name} for {pending.Path}.");
                content.ContentGuid = pending.Guid;
                pending.Content = content;
                if (folderType is not null)
                {
                    issues.AddRange(Operations.CreateOperation.Placement(call, pending.Type, folderType, folder?.ContentLink.ID ?? 0, creating: true, folder).Select(i => At(pending.Path, i)));
                }
                if (offered is not null && !offered.Contains(pending.Type.ID))
                {
                    issues.Add(new ValidationIssue(pending.Path, $"The CMS edit UI doesn't offer {pending.Type.Name} for a new block of {What(owner)}."));
                }
            }
            else
            {
                pending.Name = string.IsNullOrWhiteSpace(pending.GivenName) ? pending.Existing!.Name : pending.GivenName.Trim();
                pending.Content = (IContent)((IReadOnly)pending.Existing!).CreateWritableClone();
            }
            pending.Content.Name = pending.Name;
            Fill(pending.Content, pending.Values);
            project?.Check(pending.Content);
        }
        using (AsInline(owner))
        {
            foreach (var pending in _pending)
            {
                issues.AddRange(ValidationErrors.Validate(call, pending.Content!, SaveAction.Publish).Select(i => At(pending.Path, i)));
            }
        }
        if (_pending.Where(p => p.IsNew).Select(p => p.Path).ToList() is { Count: > 0 } made)
        {
            issues.Add(new ValidationIssue(null,
                $"The site's edit UI makes new blocks in a ContentArea shared (UIOptions.InlineBlocksInContentAreaEnabled is off), so {string.Join(", ", made)} {(made.Count == 1 ? "is a shared block" : "are shared blocks")} in the {(owner is PageData ? "page's \"For this page\"" : owner is BlockData ? "block's \"For this block\"" : "content's \"For this page\"")} folder.",
                "info"));
        }
        return issues;
    }

    /// <summary>
    /// For a write that doesn't publish: a note for each block it changes that has a published version. The CMS's preview
    /// of the content (and its on-page edit) shows a block's published version where it has one, not a newer draft, so
    /// the content's preview won't show the change until the block is published: a publish of the content through opticli
    /// publishes it along.
    /// </summary>
    public IEnumerable<ValidationIssue> PreviewNotes(SaveAction ownerAction)
    {
        if (WriteFlow.Kind(ownerAction) is SaveAction.Publish or SaveAction.Schedule)
        {
            yield break;
        }
        foreach (var pending in _pending.Where(p => !p.IsNew))
        {
            if (ContentLocator.PublishedVersion(locator.Versions(pending.Existing!.ContentLink, Language)) is not null)
            {
                yield return new ValidationIssue(pending.Path,
                    $"The block {pending.Existing.ContentLink.ToReferenceWithoutVersion()} ('{pending.Name}') is published, and its change is a draft: the preview of the content shows the block's published version until the block is published. Publishing the content through opticli publishes it along.",
                    "info");
            }
        }
    }

    /// <summary>
    /// Where each block is once every change of the write is made: its item's place in the content (a later add or move
    /// of the same write shifts it). A taken-over block no item shows any longer isn't changed.
    /// </summary>
    /// <exception cref="AgentException">
    /// <c>usage</c> for a new block in an area opticli doesn't reach here: one in a Visual Builder element of a CMS 13
    /// composition, whose item would keep pointing at nothing.
    /// </exception>
    private void Place(IContent owner)
    {
        var places = Located(owner, "").ToList();
        foreach (var pending in _pending.ToList())
        {
            var place = pending.IsNew
                ? places.FirstOrDefault(p => ReferenceEquals(Of(p.Item), pending))
                : places.FirstOrDefault(p => InlineBlocks.Of(p.Item) is null && !ContentReference.IsNullOrEmpty(p.Item.ContentLink)
                    && p.Item.ContentLink.ID == pending.Existing!.ContentLink.ID);
            if (place.Item is null && !pending.IsNew)
            {
                _pending.Remove(pending);
                continue;
            }
            if (place.Item is null)
            {
                throw AgentException.Usage($"{pending.Path}: a new block there can't be made a shared block in the content's \"For this page\" folder, as the site's edit UI makes them: opticli doesn't reach that ContentArea (one in a Visual Builder element, say) once the block is saved.",
                    "Make the block first (a shared block in the folder: block create --for, or create_content with forContent) and add it by its ref. Nothing was saved.");
            }
            pending.Path = place.Path;
        }
    }

    /// <summary>
    /// While validating: the items of new blocks show their values as inline blocks, so the CMS's own checks of the areas
    /// (<c>[AllowedTypes]</c>, the site's validators) see them as they see a block an item shows. Dispose to put the items
    /// back. Null when nothing changes (no new blocks, or a CMS without inline blocks, where the area's own types aren't
    /// checked for them before the content's save).
    /// </summary>
    public IDisposable? AsInline(IContent owner)
    {
        if (!InlineBlocks.Supported || !_pending.Any(p => p.IsNew))
        {
            return null;
        }
        var shown = Reachable(owner).Concat(_pending.SelectMany(p => Reachable(p.Values))).Select(item => (Item: item, Pending: Of(item)))
            .Where(s => s.Pending is not null).DistinctBy(s => s.Item, ReferenceEqualityComparer.Instance).ToList();
        foreach (var (item, pending) in shown)
        {
            InlineBlocks.Set(item, pending!.Values, pending.Type.ID);
        }
        return new Restore(() =>
        {
            foreach (var (item, pending) in shown)
            {
                InlineBlocks.Clear(item);
                item.ContentGuid = pending!.Guid;
            }
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>
    /// What a dry run reports: each block as it would be saved, with the status the write's save gives it (a publish's
    /// publishes it along). No ref for a block it would make, the block's own id for one it would change.
    /// </summary>
    public List<SharedBlockResult> Planned(SaveAction ownerAction) => _pending.Select(p => new SharedBlockResult
    {
        Path = p.Path,
        Ref = p.IsNew ? null : p.Existing!.ContentLink.ID.ToString(CultureInfo.InvariantCulture),
        Name = p.Name,
        Type = p.Type.Name,
        Folder = Folder()?.ContentLink.ID.ToString(CultureInfo.InvariantCulture),
        Status = StatusName(WriteFlow.Kind(ownerAction) switch
        {
            SaveAction.Publish => VersionStatus.Published,
            SaveAction.Schedule => VersionStatus.DelayedPublish,
            _ => VersionStatus.CheckedOut,
        }),
        Action = p.IsNew ? SharedBlockActions.Created : SharedBlockActions.Updated,
    }).ToList();

    /// <summary>
    /// Saves the folder (made first for new content, as phase 0 settled: its <c>ContentAssetsID</c> is set on the content
    /// before its own save) and the blocks, innermost first (an outer block's area needs the inner block's ref), each a
    /// draft, into <paramref name="project"/> when the write has one; then points their area items at them.
    /// </summary>
    /// <param name="ownerAction">The content's save action: a project item is marked ready as the content is.</param>
    /// <param name="warnings">Gets a warning when a block's project step failed (the block is saved all the same).</param>
    /// <exception cref="AgentException">A save that failed, with the blocks saved before it (<see cref="AgentException.MadeBlocks"/>).</exception>
    public List<SharedBlockResult> Save(IContent owner, ProjectSave? project, SaveAction ownerAction, List<ValidationIssue> warnings)
    {
        var results = new List<SharedBlockResult>();
        if (_pending.Count == 0)
        {
            return results;
        }
        try
        {
            var folder = Folder() ?? MakeFolder(owner);
            var folderId = folder.ContentLink.ID.ToString(CultureInfo.InvariantCulture);
            var done = new HashSet<Pending>(ReferenceEqualityComparer.Instance);
            void SaveOne(Pending pending)
            {
                if (!done.Add(pending))
                {
                    return;
                }
                foreach (var inner in Reachable(pending.Values).Select(Of).OfType<Pending>().Where(p => !ReferenceEquals(p, pending)).ToList())
                {
                    SaveOne(inner);
                }
                var content = pending.Content!;
                Fill(content, pending.Values);
                if (pending.IsNew)
                {
                    content.ParentLink = folder.ContentLink;
                }
                var link = call.SaveAsset(content, pending.IsNew ? SaveAction.Save : SaveAction.Save | SaveAction.ForceNewVersion);
                if (!pending.IsNew && link.WorkID > 0)
                {
                    // As for the content: edit mode opens the primary draft.
                    call.Service<IContentVersionRepository>().SetCommonDraft(link);
                }
                if (pending.IsNew)
                {
                    PointAt(owner, pending, link);
                }
                var stored = locator.Repository.Get<IContent>(link);
                if (project?.Add(stored, ownerAction) is { Error: { } error })
                {
                    warnings.Add(new ValidationIssue(pending.Path, $"The block {stored.ContentLink} was saved, but not put in the project: {error}", "warning"));
                    stored = locator.Repository.Get<IContent>(link);
                }
                results.Add(Result(pending.Path, stored, pending.Type.Name, folderId, pending.IsNew ? SharedBlockActions.Created : SharedBlockActions.Updated));
            }
            foreach (var pending in _pending)
            {
                SaveOne(pending);
            }
            return results;
        }
        catch (Exception ex) when (results.Count > 0 || _madeFolder is not null)
        {
            throw WithMadeBlocks(ex, results, _madeFolder);
        }
    }

    /// <summary>The folder made for new content, if this write made one.</summary>
    private IContent? _madeFolder;

    /// <summary>
    /// New content's folder, before the content itself is saved: as <c>ContentAssetHelper.GetOrCreateAssetFolder</c> makes
    /// one (below the Content Assets root, attached to the owner, published unchecked), which can't be asked for content
    /// that isn't saved. <see cref="ContentAssetFolder.Attach"/> sets the content's <c>ContentAssetsID</c>, which its first
    /// save stores. Existing content gets its folder from the CMS.
    /// </summary>
    private IContent MakeFolder(IContent owner)
    {
        if (!ContentReference.IsNullOrEmpty(owner.ContentLink))
        {
            _folder = call.Service<ContentAssetHelper>().GetOrCreateAssetFolder(owner.ContentLink.ToReferenceWithoutVersion());
            return _folder;
        }
        var type = call.Service<IContentTypeRepository>().Load(typeof(ContentAssetFolder))
            ?? throw new InvalidOperationException("The CMS has no ContentAssetFolder type.");
        var folder = locator.Repository.GetDefault<ContentAssetFolder>(AssetsRoot(owner), type.ID);
        if (owner.ContentGuid == Guid.Empty)
        {
            owner.ContentGuid = Guid.NewGuid();
        }
        if (folder.ContentGuid == Guid.Empty)
        {
            folder.ContentGuid = Guid.NewGuid();
        }
        folder.Attach(owner);
        var link = call.SaveAsset(folder, SaveAction.Publish);
        _folder = _madeFolder = locator.Repository.Get<IContent>(link);
        _folderRead = true;
        return _folder;
    }

    /// <summary>The Content Assets root; the owner's parent where the CMS has none (a test site).</summary>
    private ContentReference AssetsRoot(IContent owner) =>
        CmsApi.ContentAssetsRoot(call.Services) is { } root && !ContentReference.IsNullOrEmpty(root) ? root : owner.ParentLink;

    /// <summary>Points the items that show <paramref name="pending"/> (by its GUID) at the saved block.</summary>
    private void PointAt(IContent owner, Pending pending, ContentReference link)
    {
        foreach (var item in Reachable(owner).Concat(_pending.SelectMany(p => Reachable(p.Values))).Where(i => ReferenceEquals(Of(i), pending)).ToList())
        {
            // The GUID first: on CMS 12.0 setting the link doesn't clear it, on later versions setting the GUID clears the link.
            item.ContentGuid = Guid.Empty;
            item.ContentLink = link.ToReferenceWithoutVersion();
        }
    }

    /// <summary>
    /// After a publish (or scheduled publish) of <paramref name="published"/> through opticli: the drafts of the blocks of
    /// its own "For this page" folder that its areas show (and the areas of those blocks show, at any depth), published or
    /// scheduled along. Only those: a block from anywhere else is never published along. A block's draft another person
    /// saved goes along only when <paramref name="othersConfirmed"/> (<c>includeDraft</c>); one under an approval sequence,
    /// or that doesn't pass validation, is left as it is. Each left out gets a warning.
    /// </summary>
    /// <param name="kind"><see cref="SaveAction.Publish"/> or <see cref="SaveAction.Schedule"/>.</param>
    /// <param name="at">For a schedule: when.</param>
    /// <param name="dryRun">Only report what would be published.</param>
    /// <param name="except">Blocks to leave out (a project publish's own items).</param>
    public List<SharedBlockResult> PublishAlong(IContent published, SaveAction kind, DateTime? at, bool othersConfirmed, bool dryRun, List<ValidationIssue> warnings,
        IReadOnlySet<int>? except = null)
    {
        var results = new List<SharedBlockResult>();
        if (ContentReference.IsNullOrEmpty(published.ContentLink)
            || call.Service<ContentAssetHelper>().GetAssetFolder(published.ContentLink.ToReferenceWithoutVersion()) is not { } folder)
        {
            return results;
        }
        var language = published is ILocalizable { Language: { } own } && !CultureInfo.InvariantCulture.Equals(own) ? own : null;
        var folderId = folder.ContentLink.ID.ToString(CultureInfo.InvariantCulture);
        var seen = new HashSet<int>();
        var queue = new Queue<(IContentData Content, string Path)>([(published, "")]);
        while (queue.Count > 0)
        {
            var (content, prefix) = queue.Dequeue();
            foreach (var (item, path) in Located(content, prefix))
            {
                if (ContentReference.IsNullOrEmpty(item.ContentLink) || InlineBlocks.Of(item) is not null || !seen.Add(item.ContentLink.ID)
                    || !locator.Repository.TryGet<IContent>(item.ContentLink.ToReferenceWithoutVersion(), new LoaderOptions { LanguageLoaderOption.FallbackWithMaster() }, out var block)
                    || block is not BlockData || !block.ParentLink.CompareToIgnoreWorkID(folder.ContentLink))
                {
                    continue;
                }
                var branch = locator.Versions(block.ContentLink, language);
                if (branch.Count == 0)
                {
                    continue;
                }
                var latest = branch[0];
                var version = locator.Repository.Get<IContent>(latest.ContentLink);
                queue.Enqueue((version, path));
                if (latest.Status is not (VersionStatus.CheckedOut or VersionStatus.CheckedIn) || except?.Contains(block.ContentLink.ID) == true)
                {
                    continue;
                }
                var what = $"The block {latest.ContentLink} ('{version.Name}') at {path}";
                if (!othersConfirmed && !PendingDrafts.SavedBy(latest.SavedBy, call.UserName))
                {
                    warnings.Add(new ValidationIssue(path, $"{what} is a draft {(string.IsNullOrWhiteSpace(latest.SavedBy) ? "saved without a user name" : $"{latest.SavedBy} saved")}, so it wasn't published along: those changes aren't yours. Publish it on its own if they should go live.", "warning"));
                    continue;
                }
                if (Approvals.Applying(call, block.ContentLink) is not null)
                {
                    warnings.Add(new ValidationIssue(path, $"{what} has an approval sequence, so it wasn't published along; send it for review on its own.", "warning"));
                    continue;
                }
                var writable = (IContent)((IReadOnly)version).CreateWritableClone();
                if (ValidationErrors.Validate(call, writable, kind) is var issues && ValidationErrors.HasErrors(issues))
                {
                    warnings.Add(new ValidationIssue(path, $"{what} doesn't pass validation, so it wasn't published along: {string.Join(" ", issues.Where(i => i.Severity == "error").Select(i => i.Message))}", "warning"));
                    continue;
                }
                if (dryRun)
                {
                    results.Add(new SharedBlockResult
                    {
                        Path = path,
                        Ref = latest.ContentLink.ToString(),
                        Name = version.Name,
                        Type = call.Service<IContentTypeRepository>().Load(version.ContentTypeID)?.Name ?? version.ContentTypeID.ToString(CultureInfo.InvariantCulture),
                        Folder = folderId,
                        Status = StatusName(kind == SaveAction.Schedule ? VersionStatus.DelayedPublish : VersionStatus.Published),
                        Action = SharedBlockActions.Published,
                    });
                    continue;
                }
                if (kind == SaveAction.Schedule && writable is IVersionable versionable)
                {
                    versionable.StartPublish = at?.ToLocalTime();
                }
                var saved = call.SaveAsset(writable, kind);
                results.Add(Result(path, locator.Repository.Get<IContent>(saved), null, folderId, SharedBlockActions.Published));
            }
        }
        return results;
    }

    /// <summary>
    /// <paramref name="made"/> (what the write made or changed) with what <paramref name="along"/> published of them, and
    /// the rest of <paramref name="along"/>.
    /// </summary>
    public static List<SharedBlockResult> Merge(IReadOnlyList<SharedBlockResult> made, IReadOnlyList<SharedBlockResult> along)
    {
        static string Id(string? reference) => reference?.Split('_')[0] ?? "";
        var merged = made.Select(m => along.FirstOrDefault(a => Id(a.Ref) == Id(m.Ref) && m.Ref is not null) is { } published
            ? m with { Ref = published.Ref, Status = published.Status }
            : m).ToList();
        merged.AddRange(along.Where(a => !made.Any(m => m.Ref is not null && Id(m.Ref) == Id(a.Ref))));
        return merged;
    }

    /// <summary>The items reachable from <paramref name="root"/>'s values, with their place: <c>MainArea[2]</c>, deeper inside inline and local blocks.</summary>
    private IEnumerable<(ContentAreaItem Item, string Path)> Located(IContentData root, string prefix, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            yield break;
        }
        foreach (var property in root.Property.Where(p => !p.IsMetaData).ToList())
        {
            var at = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            switch (property.Value)
            {
                case ContentArea area:
                    var items = area.Items.ToList();
                    for (var i = 0; i < items.Count; i++)
                    {
                        yield return (items[i], $"{at}[{i}]");
                        var inner = InlineBlocks.Of(items[i]) ?? (IContentData?)Of(items[i])?.Values;
                        if (inner is not null)
                        {
                            foreach (var nested in Located(inner, $"{at}[{i}]", depth + 1))
                            {
                                yield return nested;
                            }
                        }
                    }
                    break;
                case BlockData local and not IContent:
                    foreach (var nested in Located(local, at, depth + 1))
                    {
                        yield return nested;
                    }
                    break;
            }
        }
    }

    /// <summary>The ContentArea items reachable from <paramref name="root"/>: in its areas, and inside inline, local and new blocks.</summary>
    internal IEnumerable<ContentAreaItem> Reachable(IContentData root) => Located(root, "").Select(l => l.Item);

    private const int MaxDepth = 20;

    /// <summary>Copies the block's values onto the content that is saved.</summary>
    private static void Fill(IContent content, BlockData values)
    {
        foreach (var property in values.Property.Where(p => !p.IsMetaData))
        {
            if (content.Property[property.Name] is not { } into)
            {
                continue;
            }
            if (property.IsNull)
            {
                into.Clear();
            }
            else
            {
                into.Value = property.Value;
            }
        }
    }

    /// <summary>The CMS's generated name for a new block in the folder (<c>DefaultLocalAssetNameGenerator</c>): "{owner} {type} {n}", unused in the folder and in this write.</summary>
    private static string Generated(IContent owner, ContentType type, HashSet<string> taken)
    {
        for (var n = 1; ; n++)
        {
            var name = $"{owner.Name} {type.Name} {n.ToString(CultureInfo.InvariantCulture)}";
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    private static ValidationIssue At(string path, ValidationIssue issue) =>
        issue with { Property = issue.Property is null ? path : $"{path}.{issue.Property}" };

    private SharedBlockResult Result(string path, IContent stored, string? type, string folder, string action) => new()
    {
        Path = path,
        Ref = stored.ContentLink.ToString(),
        Name = stored.Name,
        Type = type ?? call.Service<IContentTypeRepository>().Load(stored.ContentTypeID)?.Name ?? stored.ContentTypeID.ToString(CultureInfo.InvariantCulture),
        Folder = folder,
        Status = stored is IVersionable { Status: var status } ? StatusName(status) : null,
        Action = action,
    };

    private static string StatusName(VersionStatus status) => JsonNamingPolicy.CamelCase.ConvertName(status.ToString());

    private static string What(IContent content) => ContentReference.IsNullOrEmpty(content.ContentLink)
        ? $"The new content '{content.Name}'"
        : $"{content.ContentLink.ID} ('{content.Name}')";

    /// <summary>
    /// <paramref name="failure"/> as the error a caller gets, with the blocks (and folder) saved before it, which stay:
    /// deleting is off for editors, and a retry can give their refs.
    /// </summary>
    public static AgentException WithMadeBlocks(Exception failure, IReadOnlyList<SharedBlockResult> made, IContent? folder)
    {
        var kept = made.Count > 0
            ? $"The shared block(s) {string.Join(", ", made.Select(m => $"{m.Ref} at {m.Path}"))} were saved and stay: give their refs ({{\"ref\": \"...\"}}) when you retry, rather than making them again."
            : $"The \"For this page\" folder {folder?.ContentLink} was made and stays.";
        return failure switch
        {
            AgentException agent => new AgentException(agent.Code, agent.Message, agent.Hint is null ? kept : $"{agent.Hint} {kept}")
            {
                Validation = agent.Validation,
                CurrentVersion = agent.CurrentVersion,
                PendingDraft = agent.PendingDraft,
                Reason = agent.Reason,
                Project = agent.Project,
                ForcedStatus = agent.ForcedStatus,
                MadeBlocks = made,
            },
            System.ComponentModel.DataAnnotations.ValidationException validation => new AgentException(AgentErrorCodes.Validation,
                $"Validation failed: {validation.Message}", kept)
            {
                Validation = ValidationErrors.From(validation),
                MadeBlocks = made,
            },
            _ => new AgentException(AgentErrorCodes.Internal, $"The save failed: {failure.Message} ({failure.GetType().FullName})", kept) { MadeBlocks = made },
        };
    }
}
