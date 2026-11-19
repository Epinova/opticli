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
/// opticli publishes the drafts of the folder's blocks its areas show that were never published, and only those; a live
/// block's newer draft goes along only when the publish names it, or the write that publishes changed it
/// (<see cref="AlongChoice"/>); of content whose version is published already, only blocks named or written go along
/// (<see cref="AlongChoice.NamedOnly"/>). A block the write takes over whose latest version is someone else's draft needs
/// their draft confirmed (<see cref="RequireOwnDraft"/>).</para>
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

        /// <summary>
        /// In a project write whose content is marked ready or sent for review: how the block follows it once the content
        /// is saved (<see cref="ProjectSave.BlockAction"/>), <see cref="SaveAction.CheckIn"/> or
        /// <see cref="SaveAction.RequestApproval"/>; null to leave it a draft in the project.
        /// </summary>
        public SaveAction? InProject { get; set; }

        /// <summary>The version <see cref="Save"/> saved.</summary>
        public ContentReference? Saved { get; set; }
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
    /// <param name="ownerAction">The content's save action: in a project, what its blocks follow (<see cref="ProjectSave.BlockAction"/>).</param>
    /// <param name="othersConfirmed">
    /// <c>includeDraft</c>: a block taken over may get its new version over a draft someone else saved
    /// (<see cref="RequireOwnDraft"/>); the result then says whose draft it replaced.
    /// </param>
    public List<ValidationIssue> Prepare(IContent owner, ProjectSave? project, SaveAction ownerAction, bool othersConfirmed = false)
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
                // Saved unchecked like a new block, but it is a block of its own, which may have access rights of its own:
                // an editor changes it only where the edit UI would let them.
                if (!call.Allows(pending.Existing!, AccessLevel.Edit))
                {
                    var link = pending.Existing!.ContentLink.ToReferenceWithoutVersion();
                    throw AgentException.Refused(
                        $"{pending.Path} changes the shared block {link} ('{pending.Existing.Name}') of the content's \"For this page\" folder, and you don't have Edit access to that block.",
                        $"Give that item back as {{\"ref\": \"{link}\"}} to keep the block as it is, or ask a CMS administrator for the access. Nothing was saved.");
                }
                if (RequireOwnDraft(pending, othersConfirmed) is { } replaced)
                {
                    issues.Add(replaced);
                }
                pending.Name = string.IsNullOrWhiteSpace(pending.GivenName) ? pending.Existing!.Name : pending.GivenName.Trim();
                pending.Content = (IContent)((IReadOnly)pending.Existing!).CreateWritableClone();
            }
            pending.Content.Name = pending.Name;
            Fill(pending.Content, pending.Values);
            if (project is not null)
            {
                project.Check(pending.Content);
                // The approval sequence that will apply to it: its own (inherited through the folder from the content), or for
                // a new block the folder's, which inherits the content's (on new content, what the content gets from its parent).
                pending.InProject = project.BlockAction(
                    pending.IsNew ? folder?.ContentLink ?? (isNew ? owner.ParentLink : owner.ContentLink) : pending.Existing!.ContentLink,
                    ownerAction, $"{pending.Path} ({(pending.IsNew ? "a new block" : $"block {pending.Existing!.ContentLink.ToReferenceWithoutVersion()}")})");
            }
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
    /// A block taken over gets a new version based on its latest one, with every value the area item gives: when that
    /// latest version is a draft someone else saved, the write would save over their changes (decision 14 of the plan), as
    /// a publish would put someone else's draft of the content live. So, as for that publish, it needs the caller's
    /// confirmation (<c>includeDraft</c>); a dry run is refused alike, so the assistant asks before it shows the change.
    /// </summary>
    /// <returns>With the confirmation, a warning saying whose draft the write replaces; null when the draft is the caller's own.</returns>
    /// <exception cref="AgentException"><c>conflict</c> with <see cref="AgentException.PendingDraft"/> (its <see cref="PendingDraft.Path"/> set).</exception>
    private ValidationIssue? RequireOwnDraft(Pending pending, bool confirmed)
    {
        var existing = pending.Existing!;
        var branch = locator.Versions(existing.ContentLink, Language);
        if (branch.FirstOrDefault(v => v.ContentLink.WorkID == existing.ContentLink.WorkID) is not { } latest
            || latest.Status is VersionStatus.Published or VersionStatus.PreviouslyPublished
            || PendingDrafts.SavedBy(latest.SavedBy, call.UserName))
        {
            return null;
        }
        var block = existing.ContentLink.ToReferenceWithoutVersion().ToString();
        var published = ContentLocator.PublishedVersion(branch) is { } publishedId
            ? PropertyValues.Snapshot(locator.Repository.Get<IContent>(new ContentReference(existing.ContentLink.ID, publishedId)))
            : new Dictionary<string, JsonElement?>();
        var draft = new PendingDraft(latest.ContentLink.ToString(), latest.SavedBy, latest.Saved.ToUniversalTime(),
            call.Properties.Shown(existing, PropertyValues.Diff(published, PropertyValues.Snapshot(existing))))
        {
            Path = pending.Path,
        };
        var what = $"the block {block} ('{existing.Name}') of the content's \"For this page\" folder at {pending.Path}";
        if (!confirmed)
        {
            throw new AgentException(AgentErrorCodes.Conflict,
                $"The write would save a new version of {what} over {draft.Describe()}: the area item gives all of the block's values, so their changes wouldn't be in it.",
                call.ForCaller(
                    $"Ask whether their changes may be replaced; if so, retry with --include-draft (in a plan: \"includeDraft\": true on the step). To leave the block as it is, give that item back as {{\"ref\": \"{block}\"}}; to change it on top of their draft, set it by its ref (opticli set {block} ...). Nothing was saved.",
                    $"Ask whether their changes may be replaced; if so, retry with includeDraft. To leave the block as it is, give that item back as {{\"ref\": \"{block}\"}}; to change it on top of their draft, update_content on {block}. Nothing was saved."))
            {
                PendingDraft = draft,
            };
        }
        return new ValidationIssue(pending.Path,
            $"The block {block} ('{existing.Name}') gets its new version over {draft.Describe()}, as includeDraft confirmed: their changes stay only in that version, in the block's history.",
            "warning");
    }

    /// <summary>
    /// For a write that doesn't publish: a note for each block it changes that has a published version. The CMS's preview
    /// of the content (and its on-page edit) shows a block's published version where it has one, not a newer draft, so
    /// the content's preview won't show the change until the block is published: on its own, or along with the content
    /// when its publish names the block (a write that publishes takes the blocks it changed along by itself).
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
                var block = pending.Existing.ContentLink.ToReferenceWithoutVersion();
                yield return new ValidationIssue(pending.Path,
                    $"The block {block} ('{pending.Name}') is published, and its change is a draft: the preview of the content shows the block's published version until the block is published. "
                    + call.ForCaller(
                        $"A publish of the content leaves it a draft unless it names the block (opticli publish {Owner!.ContentLink.ID.ToString(CultureInfo.InvariantCulture)} --with-block {block}), or publish the block on its own (opticli publish {block}).",
                        $"A publish of the content leaves it a draft unless it names the block (publish_content with blocks: [\"{block}\"]), or publish the block on its own."),
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
        var shown = Slots(owner).Select(slot => (Slot: slot, Pending: Of(slot.Item))).Where(s => s.Pending is not null).ToList();
        foreach (var ((_, area, index), pending) in shown)
        {
            var inline = Copy(area.Items[index]);
            InlineBlocks.Set(inline, pending!.Values, pending.Type.ID);
            area.Items[index] = inline;
        }
        return new Restore(() =>
        {
            foreach (var ((item, area, index), _) in shown)
            {
                area.Items[index] = item;
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
            _ => p.InProject switch
            {
                SaveAction.CheckIn => VersionStatus.CheckedIn,
                SaveAction.RequestApproval => VersionStatus.AwaitingApproval,
                _ => VersionStatus.CheckedOut,
            },
        }),
        Action = p.IsNew ? SharedBlockActions.Created : SharedBlockActions.Updated,
    }).ToList();

    /// <summary>
    /// Saves the folder (made first for new content, as phase 0 settled: its <c>ContentAssetsID</c> is set on the content
    /// before its own save) and the blocks, innermost first (an outer block's area needs the inner block's ref), each a
    /// draft, into <paramref name="project"/> when the write has one; then points their area items at them. In a project they
    /// go in as drafts: they follow the content (marked ready, or sent for review) once it is saved
    /// (<see cref="FollowInProject"/>), when the folder's access rights and approval sequence are the content's.
    /// </summary>
    /// <param name="ownerAction">The content's save action.</param>
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
                pending.Saved = link;
                var stored = locator.Repository.Get<IContent>(link);
                if (project?.Add(stored, ownerAction, markReady: false) is { Error: { } error })
                {
                    pending.InProject = null;
                    warnings.Add(new ValidationIssue(pending.Path, $"The block {stored.ContentLink} was saved, but not put in the project: {error}", "warning"));
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

    /// <summary>
    /// After the content's save into <paramref name="project"/>, which marked it ready or sent it for review: each block the
    /// write made or changed follows it (<see cref="Pending.InProject"/>), marked ready where no approval sequence applies
    /// to it and sent for review where one does, so the project's publish (or its reviewers) put the content and its blocks
    /// live together. A block the CMS doesn't take that way stays a draft in the project, with a warning.
    /// </summary>
    /// <param name="made">What <see cref="Save"/> reported; returned with each block's status as it is now.</param>
    public List<SharedBlockResult> FollowInProject(ProjectSave project, List<SharedBlockResult> made, List<ValidationIssue> warnings)
    {
        foreach (var pending in _pending.Where(p => p.InProject is not null && p.Saved is not null))
        {
            var link = pending.Saved!;
            var stored = locator.Repository.Get<IContent>(link);
            if (stored is not IVersionable { Status: VersionStatus.CheckedOut or VersionStatus.Rejected })
            {
                continue;
            }
            if (project.Follow(stored, pending.InProject!.Value) is { } error)
            {
                warnings.Add(new ValidationIssue(pending.Path,
                    $"The block {link} is in the project as a draft: it couldn't be {(pending.InProject == SaveAction.CheckIn ? "marked Ready to publish" : "sent for review")} with the content: {error}",
                    "warning"));
                continue;
            }
            var now = locator.Repository.Get<IContent>(link);
            made = made.Select(m => m.Ref is not null && m.Ref.Split('_')[0] == link.ID.ToString(CultureInfo.InvariantCulture)
                ? m with { Ref = now.ContentLink.ToString(), Status = now is IVersionable { Status: var status } ? StatusName(status) : m.Status }
                : m).ToList();
        }
        return made;
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
        foreach (var (item, area, index) in Slots(owner).Where(s => ReferenceEquals(Of(s.Item), pending)).ToList())
        {
            var linked = Copy(item);
            linked.ContentLink = link.ToReferenceWithoutVersion();
            area.Items[index] = linked;
        }
    }

    /// <summary>
    /// The places of the items reachable from the owner and from the new blocks: each item, its area and its position, to
    /// put another item in its place. An item is replaced, never changed in place: CMS 12's ContentArea keeps a fragment
    /// per item that it updates from the item's change events by the item's position, which an item a copy of the area
    /// took the place of no longer has.
    /// </summary>
    private List<(ContentAreaItem Item, ContentArea Area, int Index)> Slots(IContent owner) =>
        Located(owner, "").Concat(_pending.SelectMany(p => Located(p.Values, "")))
            .DistinctBy(l => l.Item, ReferenceEqualityComparer.Instance)
            .Select(l => (l.Item, l.Area, l.Index))
            .ToList();

    /// <summary>A new item with <paramref name="item"/>'s render settings and personalization, and no content yet.</summary>
    private static ContentAreaItem Copy(ContentAreaItem item)
    {
        var copy = new ContentAreaItem();
        foreach (var (key, value) in CmsApi.RenderSettings(item))
        {
            CmsApi.SetRenderSetting(copy, key, value);
        }
        if (!string.IsNullOrEmpty(item.ContentGroup))
        {
            copy.ContentGroup = item.ContentGroup;
        }
        if (item.AllowedRoles?.Any() == true)
        {
            copy.AllowedRoles = item.AllowedRoles.ToList();
        }
        return copy;
    }

    /// <summary>
    /// Which drafts of the folder's blocks a publish takes along besides those of blocks never published, and which it
    /// keeps back (decision 11 of the plan): by default only a block that was never published in the language goes along,
    /// as it can't show until it is; a block that is live keeps its newer draft, unless the caller names it
    /// (<see cref="With"/>) or the write that publishes changed it itself (<see cref="Written"/>).
    /// </summary>
    internal sealed record AlongChoice
    {
        public static readonly AlongChoice Default = new();

        /// <summary>Live blocks whose drafts go along all the same (content ids): <c>--with-block</c>, <c>blocks</c>.</summary>
        public IReadOnlySet<int> With { get; init; } = new HashSet<int>();

        /// <summary>Blocks kept back although they were never published (content ids): <c>--without-block</c>, <c>skipBlocks</c>.</summary>
        public IReadOnlySet<int> Without { get; init; } = new HashSet<int>();

        /// <summary>The blocks the write that publishes made or changed itself (content ids): they go along unnamed.</summary>
        public IReadOnlySet<int> Written { get; init; } = new HashSet<int>();

        /// <summary>
        /// The block versions earlier steps of a plan run saved (content id, version id: <see cref="PublishRequest.WrittenBlocks"/>):
        /// a block whose latest version is one of them goes along unnamed, as <see cref="Written"/> does.
        /// </summary>
        public IReadOnlySet<(int Id, int Version)> WrittenVersions { get; init; } = new HashSet<(int, int)>();

        /// <summary>How the caller takes a live block's draft along (given its id): the last sentence of its warning.</summary>
        public Func<string, string>? HowToInclude { get; init; }

        /// <summary>
        /// A publish of content whose version is published already (decision 13 of the plan): only the blocks named
        /// (<see cref="With"/>) or written (<see cref="Wrote"/>) go along. A block never published stays a draft as well:
        /// it isn't new with this publish, so someone probably kept it back. A publish of a new version of the content keeps
        /// the default, which takes those along.
        /// </summary>
        public bool NamedOnly { get; init; }

        public bool Names => With.Count > 0 || Without.Count > 0;

        /// <summary>Whether the publish wrote the block itself: the write that publishes changed it, or an earlier step of the plan run saved its latest version.</summary>
        public bool Wrote(int id, ContentReference latest) => Written.Contains(id) || WrittenVersions.Contains((id, latest.WorkID));

        /// <summary>
        /// The block versions earlier steps of a plan run saved (<c>941_5108</c>), as <see cref="WrittenVersions"/>.
        /// </summary>
        /// <exception cref="AgentException"><c>usage</c> for one that names no version.</exception>
        public static HashSet<(int Id, int Version)> Versions(IReadOnlyList<string>? refs)
        {
            var versions = new HashSet<(int, int)>();
            foreach (var reference in refs ?? [])
            {
                if (!ContentReference.TryParse(reference?.Trim(), out var link) || link.ID <= 0 || link.WorkID <= 0)
                {
                    throw AgentException.Usage($"writtenBlocks: '{reference}' is not a block version (id_version).");
                }
                versions.Add((link.ID, link.WorkID));
            }
            return versions;
        }
    }

    /// <summary>
    /// A block of the folder whose draft a publish left behind because it wasn't named (<see cref="PublishAlong"/>): one a
    /// publish of the content takes along when named.
    /// </summary>
    /// <param name="Live">The block has a published version (its newer draft stays); else it was never published.</param>
    internal sealed record Nameable(string Path, int Id, string Name, bool Live);

    /// <summary>
    /// The hint of a publish of content published already that took nothing along: the blocks of its folder it could take
    /// along when named, and how to name them.
    /// </summary>
    public string NameableHint(IContent content, IReadOnlyList<Nameable> blocks)
    {
        var owner = content.ContentLink.ID.ToString(CultureInfo.InvariantCulture);
        var ids = blocks.Select(b => b.Id.ToString(CultureInfo.InvariantCulture)).ToList();
        var listed = string.Join("; ", blocks.Select(b =>
            $"{b.Id.ToString(CultureInfo.InvariantCulture)} ('{b.Name}') at {b.Path}, {(b.Live ? "published, with a newer draft" : "never published")}"));
        return $"Drafts of blocks of its \"For this page\" folder that it shows, which go along when named: {listed}. " + call.ForCaller(
            $"To publish those that should go live: opticli publish {owner} {string.Join(" ", ids.Select(i => $"--with-block {i}"))} (name only those), or publish a block on its own (opticli publish <ref>).",
            $"To publish those that should go live: publish_content on {owner} with blocks: [{string.Join(", ", ids.Select(i => $"\"{i}\""))}] (name only those), or publish_content on a block's ref.");
    }

    /// <summary>
    /// The blocks a publish names (<see cref="AlongChoice.With"/>, <see cref="AlongChoice.Without"/>) must be blocks of the
    /// content's own "For this page" folder that <paramref name="versions"/> show: a publish never puts a block from
    /// anywhere else live this way. Checked before anything is saved (a dry run too).
    /// </summary>
    /// <param name="withName">How the caller names the blocks to take along (<c>--with-block</c>, <c>blocks</c>).</param>
    /// <param name="withoutName">How the caller names the blocks to keep back.</param>
    /// <exception cref="AgentException"><c>usage</c> for a block named both ways, or one that isn't such a block.</exception>
    public void RequireChosen(IReadOnlyList<IContent> versions, AlongChoice choice, string withName, string withoutName)
    {
        if (!choice.Names)
        {
            return;
        }
        if (choice.With.Intersect(choice.Without).OrderBy(i => i).ToList() is { Count: > 0 } both)
        {
            throw AgentException.Usage($"{Ids(both)} {(both.Count == 1 ? "is" : "are")} named in both {withName} and {withoutName}.",
                $"Name each block in one of them only: {withName} publishes a block's draft with the content, {withoutName} keeps it a draft. Nothing was published.");
        }
        var shown = versions.SelectMany(v => OwnBlocksShown(v)).Select(b => b.Block.ContentLink.ID).ToHashSet();
        if (choice.With.Concat(choice.Without).Where(id => !shown.Contains(id)).Distinct().OrderBy(i => i).ToList() is { Count: > 0 } elsewhere)
        {
            throw AgentException.Usage(
                $"{Ids(elsewhere)} {(elsewhere.Count == 1 ? "isn't a block" : "aren't blocks")} of the content's own \"For this page\" folder that its ContentAreas show, so {(elsewhere.Count == 1 ? "it" : "they")} can't be named in {withName} or {withoutName}: only those blocks are published along.",
                call.ForCaller(
                    "Name only blocks of the content's own folder that it shows (a dry run of the publish lists them, in sharedBlocks and its warnings); publish any other block on its own (opticli publish <ref>). Nothing was published.",
                    "Name only blocks of the content's own folder that it shows (a dryRun of the publish lists them, in sharedBlocks and its warnings); publish any other block on its own (publish_content on its ref). Nothing was published."));
        }

        static string Ids(IReadOnlyList<int> ids) => ids.Count == 1
            ? $"Block {ids[0].ToString(CultureInfo.InvariantCulture)}"
            : $"Blocks {string.Join(", ", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)))}";
    }

    /// <summary>
    /// After a publish (or scheduled publish) of <paramref name="published"/> through opticli: the drafts of the blocks of
    /// its own "For this page" folder that its areas show (and the areas of those blocks show, at any depth), published or
    /// scheduled along, as <paramref name="choice"/> picks them: by default only blocks never published in the language.
    /// Only those: a block from anywhere else is never published along. A block's draft another person saved goes along
    /// only when <paramref name="othersConfirmed"/> (<c>includeDraft</c>); one under an approval sequence, or that doesn't
    /// pass validation, is left as it is. Each left out gets a warning.
    /// </summary>
    /// <param name="kind"><see cref="SaveAction.Publish"/> or <see cref="SaveAction.Schedule"/>.</param>
    /// <param name="at">For a schedule: when.</param>
    /// <param name="dryRun">Only report what would be published.</param>
    /// <param name="inProject">
    /// For a project publish: whether a block's draft (by content id and language) is an item of the project: null when it
    /// isn't, else whether the project publishes it (marked Ready to publish). A project's items follow the project's rules,
    /// so they are never published along; one that stays behind as a draft gets a warning.
    /// </param>
    /// <param name="choice">Which live blocks' drafts go along, and which blocks stay behind; null: <see cref="AlongChoice.Default"/>.</param>
    /// <param name="nameable">Gets each block left behind because it wasn't named (and the publish didn't write it).</param>
    public List<SharedBlockResult> PublishAlong(IContent published, SaveAction kind, DateTime? at, bool othersConfirmed, bool dryRun, List<ValidationIssue> warnings,
        Func<int, CultureInfo?, bool?>? inProject = null, AlongChoice? choice = null, List<Nameable>? nameable = null)
    {
        choice ??= AlongChoice.Default;
        var results = new List<SharedBlockResult>();
        foreach (var (path, block, latest, version, folderId, live) in OwnBlocksShown(published))
        {
            if (latest.Status is not (VersionStatus.CheckedOut or VersionStatus.CheckedIn))
            {
                continue;
            }
            var what = $"The block {latest.ContentLink} ('{version.Name}') at {path}";
            if (inProject?.Invoke(block.ContentLink.ID, (version as ILocalizable)?.Language) is { } ready)
            {
                if (!ready)
                {
                    warnings.Add(new ValidationIssue(path,
                        $"{what} is a draft in the project that isn't marked Ready to publish, so it stays a draft: the content goes live showing the block's published version (or nothing of it, where it was never published). Mark it ready and publish the project to put it live.",
                        "warning"));
                }
                continue;
            }
            var id = block.ContentLink.ID;
            if (choice.Without.Contains(id))
            {
                warnings.Add(new ValidationIssue(path, live
                    ? $"{what} stays a draft, as asked: the content goes live showing the block's published version."
                    : $"{what} was never published, and stays a draft, as asked: the content goes live without it (visitors see nothing of the block until it is published).",
                    "warning"));
                continue;
            }
            if ((live || choice.NamedOnly) && !choice.With.Contains(id) && !choice.Wrote(id, latest.ContentLink))
            {
                var reference = id.ToString(CultureInfo.InvariantCulture);
                var how = choice.HowToInclude?.Invoke(reference) ?? HowToInclude(published, reference);
                nameable?.Add(new Nameable(path, id, version.Name, live));
                warnings.Add(new ValidationIssue(path, live
                    ? $"{what} is published already, so its newer draft stays a draft: the content {(choice.NamedOnly ? "shows" : "goes live showing")} the block's published version. {how}"
                    : $"{what} was never published, and stays a draft: the content is published already, so a publish of it takes along only the blocks it names (or that the same plan run saved), and visitors see nothing of this block until it is published. {how}",
                    "warning"));
                continue;
            }
            // A block the write changes itself goes along as the write saves it, its own latest version; a dry run still sees
            // the draft that version will replace. A write gets this far over someone else's draft only with their draft
            // confirmed (RequireOwnDraft), and in a plan's dry run that confirmation may be the steps' that change the block
            // rather than the publish's own (DraftRequest.IncludeBlockDraft).
            if (!othersConfirmed && !(dryRun && choice.Written.Contains(id)) && !PendingDrafts.SavedBy(latest.SavedBy, call.UserName))
            {
                warnings.Add(new ValidationIssue(path, $"{what} is a draft {(string.IsNullOrWhiteSpace(latest.SavedBy) ? "saved without a user name" : $"{latest.SavedBy} saved")}, so it wasn't published along: those changes aren't yours. Publish it on its own if they should go live.", "warning"));
                continue;
            }
            if (!call.Allows(block, AccessLevel.Publish))
            {
                // Published unchecked (SaveAsset): a block with access rights of its own goes live only for someone who may publish it.
                warnings.Add(new ValidationIssue(path, $"{what} is a draft, and you don't have Publish access to that block, so it wasn't published along. Ask someone who may publish it.", "warning"));
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
        return results;
    }

    /// <summary>
    /// How to put a live block's draft that a publish of <paramref name="content"/> left behind live: by naming it in a
    /// publish of the content (which, with the content published already, publishes just the blocks), or on its own.
    /// </summary>
    private string HowToInclude(IContent content, string block)
    {
        var owner = content.ContentLink.ID.ToString(CultureInfo.InvariantCulture);
        return call.ForCaller(
            $"Name it to publish that draft with the content (opticli publish {owner} --with-block {block}), or publish it on its own (opticli publish {block}).",
            $"Publish that draft with the content (publish_content on {owner} with blocks: [\"{block}\"]), or publish the block on its own.");
    }

    /// <summary>
    /// The blocks of <paramref name="content"/>'s own "For this page" folder that its areas show (and the areas of those
    /// blocks' latest versions, at any depth), each once: its place, the block, its latest version in the content's
    /// language, that version, the folder's id, and whether the block has a published version in that language (a block
    /// published only in another language counts as never published here). Blocks from anywhere else aren't walked.
    /// </summary>
    private IEnumerable<(string Path, IContent Block, ContentVersion Latest, IContent Version, string FolderId, bool Live)> OwnBlocksShown(IContent content)
    {
        if (ContentReference.IsNullOrEmpty(content.ContentLink)
            || call.Service<ContentAssetHelper>().GetAssetFolder(content.ContentLink.ToReferenceWithoutVersion()) is not { } folder)
        {
            yield break;
        }
        var language = content is ILocalizable { Language: { } own } && !CultureInfo.InvariantCulture.Equals(own) ? own : null;
        var folderId = folder.ContentLink.ID.ToString(CultureInfo.InvariantCulture);
        var seen = new HashSet<int>();
        var queue = new Queue<(IContentData Content, string Path)>([(content, "")]);
        while (queue.Count > 0)
        {
            var (shown, prefix) = queue.Dequeue();
            foreach (var (item, path, _, _) in Located(shown, prefix).ToList())
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
                yield return (path, block, latest, version, folderId, ContentLocator.PublishedVersion(branch) is not null);
            }
        }
    }

    /// <summary>
    /// What a review request of <paramref name="version"/> would leave unpublished: the blocks this write makes or changes,
    /// and the blocks of its own "For this page" folder that its areas show and that were never published (a draft). The
    /// approval publishes the content alone. A live block with a newer draft doesn't count: the content shows its
    /// published version, as a publish leaves it unless that block is named; one whose draft an earlier step of the plan run
    /// saved does (<paramref name="written"/>), as a block the write changes does.
    /// </summary>
    /// <param name="written">The block versions earlier steps of a plan run saved (<see cref="AlongChoice.WrittenVersions"/>).</param>
    /// <returns>Each as its place and what it is (<c>MainArea[2] (a new block)</c>); empty when there are none.</returns>
    public List<string> UnpublishedForReview(IContent version, IReadOnlySet<(int Id, int Version)>? written = null)
    {
        var listed = new List<string>();
        var ids = new HashSet<int>();
        foreach (var pending in _pending)
        {
            listed.Add(pending.IsNew ? $"{pending.Path} (a new block)" : $"{pending.Path} (block {pending.Existing!.ContentLink.ToReferenceWithoutVersion()}, changed)");
            if (!pending.IsNew)
            {
                ids.Add(pending.Existing!.ContentLink.ID);
            }
        }
        foreach (var (path, block, latest, _, _, live) in OwnBlocksShown(version))
        {
            if (latest.Status is not (VersionStatus.CheckedOut or VersionStatus.CheckedIn))
            {
                continue;
            }
            if (!live && ids.Add(block.ContentLink.ID))
            {
                listed.Add($"{path} (block {block.ContentLink.ToReferenceWithoutVersion()}, never published)");
            }
            else if (written?.Contains((block.ContentLink.ID, latest.ContentLink.WorkID)) == true && ids.Add(block.ContentLink.ID))
            {
                listed.Add($"{path} (block {block.ContentLink.ToReferenceWithoutVersion()}, changed)");
            }
        }
        return listed;
    }

    /// <summary>The blocks of the folder an area add found already there (<see cref="Find"/>), to report.</summary>
    private readonly List<IContent> _found = [];

    /// <summary>
    /// A block of the owner's folder that an area add which adds only what is missing (<c>--if-missing</c>, <c>apply
    /// --update-existing</c>) found already in the area: listed in the result (<see cref="SharedBlockActions.Existing"/>),
    /// so a plan run again reaches it by its ref as it did the first time.
    /// </summary>
    public void Find(IContent block)
    {
        if (!_found.Any(f => f.ContentLink.ID == block.ContentLink.ID))
        {
            _found.Add(block);
        }
    }

    /// <summary>The blocks <see cref="Find"/> recorded, with their place in <paramref name="owner"/> (as the write leaves it).</summary>
    public List<SharedBlockResult> Found(IContent owner)
    {
        if (_found.Count == 0)
        {
            return [];
        }
        var places = Located(owner, "").ToList();
        var folder = Folder()?.ContentLink.ID.ToString(CultureInfo.InvariantCulture);
        return _found.Select(block => places.FirstOrDefault(p => InlineBlocks.Of(p.Item) is null && !ContentReference.IsNullOrEmpty(p.Item.ContentLink)
                && p.Item.ContentLink.ID == block.ContentLink.ID) is { Item: not null } place
                ? Result(place.Path, block, null, folder ?? "", SharedBlockActions.Existing) with { Folder = folder }
                : null)
            .OfType<SharedBlockResult>()
            .ToList();
    }

    /// <summary>
    /// A dry run's <paramref name="changes"/> with each block it would make shown as what it will be, not by the GUID its
    /// item carries until it is saved: <c>{type, name, properties, shared: "planned"}</c>, its values as the caller may see
    /// them (<see cref="EditUiProperties"/>), also for blocks inside it.
    /// </summary>
    public IReadOnlyList<PropertyChange> ShowPlanned(IReadOnlyList<PropertyChange> changes)
    {
        if (!_pending.Any(p => p.IsNew))
        {
            return changes;
        }
        return changes.Select(c => c.After is { ValueKind: JsonValueKind.Array or JsonValueKind.Object } after
            ? c with { After = JsonSerializer.SerializeToElement(Planned(System.Text.Json.Nodes.JsonNode.Parse(after.GetRawText()))) }
            : c).ToList();
    }

    /// <summary>A copy of <paramref name="node"/> with the items of new blocks shown as planned blocks.</summary>
    private System.Text.Json.Nodes.JsonNode? Planned(System.Text.Json.Nodes.JsonNode? node, int depth = 0)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonArray array when depth <= MaxDepth:
                return new System.Text.Json.Nodes.JsonArray(array.Select(item => Planned(item, depth + 1)).ToArray());
            case System.Text.Json.Nodes.JsonObject item when depth <= MaxDepth:
                if (item["ref"] is null && item["guid"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var guid)
                    && _pending.FirstOrDefault(p => p.IsNew && p.Guid == guid) is { } pending)
                {
                    var values = PropertyValues.Snapshot(pending.Values).Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value);
                    var shown = call.Properties.ShownValues(pending.Values, JsonSerializer.SerializeToElement(values));
                    var planned = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = pending.Type.Name,
                        ["name"] = string.IsNullOrEmpty(pending.Name) ? pending.GivenName : pending.Name,
                        ["properties"] = Planned(System.Text.Json.Nodes.JsonNode.Parse(shown.GetRawText()), depth + 1),
                    };
                    foreach (var (key, kept) in item.Where(p => p.Key is not ("guid" or "ref")))
                    {
                        planned[key] = kept?.DeepClone();
                    }
                    planned["shared"] = "planned";
                    return planned;
                }
                var copy = new System.Text.Json.Nodes.JsonObject();
                foreach (var (key, inner) in item)
                {
                    copy[key] = Planned(inner, depth + 1);
                }
                return copy;
            default:
                return node?.DeepClone();
        }
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

    /// <summary>A block of the folder this write takes over, which <paramref name="item"/> shows by its ref; null for any other item.</summary>
    private Pending? TakenOver(ContentAreaItem item) =>
        ContentReference.IsNullOrEmpty(item.ContentLink) || InlineBlocks.Of(item) is not null
            ? null
            : _pending.FirstOrDefault(p => !p.IsNew && p.Existing!.ContentLink.ID == item.ContentLink.ID);

    /// <summary>
    /// The items reachable from <paramref name="root"/>'s values, with their place: <c>MainArea[2]</c>, deeper inside
    /// inline and local blocks, the new blocks and the blocks taken over (whose values are saved with the write).
    /// </summary>
    private IEnumerable<(ContentAreaItem Item, string Path, ContentArea Area, int Index)> Located(IContentData root, string prefix, int depth = 0)
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
                        yield return (items[i], $"{at}[{i}]", area, i);
                        var inner = InlineBlocks.Of(items[i]) ?? (IContentData?)Of(items[i])?.Values ?? TakenOver(items[i])?.Values;
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
