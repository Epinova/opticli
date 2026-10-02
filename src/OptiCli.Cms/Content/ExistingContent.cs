using System.Globalization;
using System.Text.Json;
using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// Create requests with a fixed GUID that already exists: a conflict, or with <c>updateExisting</c> an update of that
/// content, so a plan can be run again against the same database.
/// </summary>
internal static class ExistingContent
{
    /// <exception cref="AgentException">
    /// <c>conflict</c> for content the caller can't read: the GUID is taken, but the conflict mustn't name the content.
    /// </exception>
    public static IContent? Find(WriteFlow flow, Guid guid)
    {
        if (!flow.Repository.TryGet<IContent>(guid, new LoaderOptions { LanguageLoaderOption.FallbackWithMaster() }, out var content))
        {
            return null;
        }
        return flow.Call.CanRead(content)
            ? content
            : throw AgentException.Conflict($"Content with GUID {guid} already exists.", "Give the new content another GUID.");
    }

    /// <param name="parent">Where the request puts the content; existing content must be there (or in the recycle bin).</param>
    /// <param name="includeDraft">Confirms that a publish also puts other people's unpublished changes live.</param>
    public static WriteResult Update(
        WriteFlow flow,
        IContent existing,
        bool updateExisting,
        ContentType type,
        IContent parent,
        CultureInfo? language,
        string name,
        IReadOnlyDictionary<string, JsonElement>? properties,
        bool publish,
        bool requestApproval,
        bool includeDraft,
        bool dryRun,
        DateTime? publishAt = null)
    {
        var link = existing.ContentLink.ToReferenceWithoutVersion();
        var existingType = flow.Types.Load(existing.ContentTypeID)?.Name ?? existing.ContentTypeID.ToString(CultureInfo.InvariantCulture);
        var deleted = existing.IsDeleted;
        if (!updateExisting)
        {
            throw AgentException.Conflict(
                $"Content with GUID {existing.ContentGuid} already exists: {link.ID} ({existingType} '{existing.Name}'{(deleted ? ", in the recycle bin" : "")}).",
                "Pass updateExisting (apply --update-existing) to update it instead, or give the new content another GUID.");
        }
        if (existing.ContentTypeID != type.ID)
        {
            throw AgentException.Conflict(
                $"Content with GUID {existing.ContentGuid} already exists as {existingType} ({link.ID} '{existing.Name}'), not as {type.Name}.",
                "Fix the plan's type or GUID; opticli won't change the type of existing content.");
        }
        if (!deleted && !existing.ParentLink.CompareToIgnoreWorkID(parent.ContentLink))
        {
            throw AgentException.Conflict(
                $"{link.ID} ('{existing.Name}') is under {existing.ParentLink.ID}, not under {parent.ContentLink.ID} where this request puts it: it was moved.",
                $"Move it back (opticli move {link.ID} --to {parent.ContentLink.ID}) or change the plan's parent.");
        }

        var branch = flow.Locator.ContentLanguage(flow.Locator.LoadAnyLanguage(link), language?.Name);
        var versions = existing is IVersionable ? flow.Locator.Versions(link, branch) : null;
        // Checked before anything changes, including the move out of the recycle bin.
        var what = $"{link.ID} ('{existing.Name}')";
        if (versions is not null)
        {
            Approvals.RequireNotInReview(versions, what, branch?.Name);
        }
        // In the recycle bin, the sequence of where it is restored to applies.
        var decision = WriteFlow.Publishing(flow.Call, deleted ? parent.ContentLink : link, publish, requestApproval, publishAt, what);
        var pending = (decision is SaveAction.Publish or SaveAction.Schedule) && versions is not null
            ? PendingDrafts.Require(
                flow.Locator.PendingDraft(versions, flow.Repository.Get<IContent>(ContentLocator.Latest(versions, link, branch).ContentLink)),
                includeDraft, dryRun, what, branch?.Name)
            : null;

        var current = versions is not null
            ? flow.Repository.Get<IContent>(ContentLocator.Latest(versions, link, branch).ContentLink)
            : branch is null ? flow.Repository.Get<IContent>(link) : flow.Repository.Get<IContent>(link, branch);

        var before = PropertyValues.Snapshot(current);
        var writable = (IContent)((IReadOnly)current).CreateWritableClone();
        writable.Name = name;
        flow.Writer.Apply(writable, properties);
        if (deleted)
        {
            // Validated where it will be once restored, not in the recycle bin.
            writable.ParentLink = parent.ContentLink;
        }

        // Restored last, once the values and validation passed, and moved back if the save fails after all.
        var restored = false;
        void Restore()
        {
            flow.Call.Move(link, parent.ContentLink);
            restored = true;
        }

        var action = writable is IVersionable ? WriteFlow.DraftAction(decision) : decision ?? SaveAction.Save;
        WriteFlow.ScheduleAt(writable, decision, publishAt);
        var cleared = WriteFlow.ClearExpiredStopPublish(writable, decision, properties);
        WriteResult result;
        try
        {
            result = flow.Save(
                writable,
                before,
                action,
                dryRun,
                ContentSummaries.Describe(current, flow.Types),
                current.ContentLink.WorkID > 0 ? current.ContentLink.WorkID : null,
                saveUnchanged: decision is not null && current is IVersionable { Status: not VersionStatus.Published },
                precheck: cleared is null ? null : [cleared],
                beforeSave: deleted ? Restore : null);
        }
        catch when (restored)
        {
            flow.Call.Delete(link);
            throw;
        }
        return result with
        {
            // Unsaved, the result describes the version as loaded, which was still in the recycle bin.
            Content = restored && !result.Saved ? ContentSummaries.Describe(flow.Repository.Get<IContent>(current.ContentLink), flow.Types) : result.Content,
            Existing = true,
            Restored = restored,
            PendingDraft = pending,
            PreviouslyPublished = result is { Saved: true, Published: true } && versions is not null ? ContentLocator.PublishedVersion(versions) : null,
        };
    }
}
