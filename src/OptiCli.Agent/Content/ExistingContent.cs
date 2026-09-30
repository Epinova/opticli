using System.Globalization;
using System.Text.Json;
using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Content;

/// <summary>
/// Create requests with a fixed GUID that already exists: a conflict, or with <c>updateExisting</c> an update of that
/// content, so a plan can be run again against the same database.
/// </summary>
internal static class ExistingContent
{
    public static IContent? Find(WriteFlow flow, Guid guid) =>
        flow.Repository.TryGet<IContent>(guid, new LoaderOptions { LanguageLoaderOption.FallbackWithMaster() }, out var content) ? content : null;

    /// <param name="parent">Where the request puts the content; existing content must be there (or in the recycle bin).</param>
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
        bool dryRun)
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

        if (deleted && !dryRun)
        {
            flow.Repository.Move(link, parent.ContentLink, AccessLevel.NoAccess, AccessLevel.NoAccess);
        }

        var branch = flow.Locator.ContentLanguage(flow.Locator.LoadAnyLanguage(link), language?.Name);
        var current = existing is IVersionable
            ? flow.Repository.Get<IContent>(flow.Locator.LatestVersion(link, branch).ContentLink)
            : branch is null ? flow.Repository.Get<IContent>(link) : flow.Repository.Get<IContent>(link, branch);

        var before = PropertyValues.Snapshot(current);
        var writable = (IContent)((IReadOnly)current).CreateWritableClone();
        writable.Name = name;
        flow.Writer.Apply(writable, properties);

        var action = writable is IVersionable ? WriteFlow.DraftAction(publish) : publish ? SaveAction.Publish : SaveAction.Save;
        var result = flow.Save(
            writable,
            before,
            action,
            dryRun,
            ContentSummaries.Describe(current, flow.Types),
            current.ContentLink.WorkID > 0 ? current.ContentLink.WorkID : null,
            saveUnchanged: publish && current is IVersionable { Status: not VersionStatus.Published });
        return result with
        {
            // Unsaved, the result describes the version as loaded, which was still in the recycle bin.
            Content = deleted && !dryRun && !result.Saved ? ContentSummaries.Describe(flow.Repository.Get<IContent>(current.ContentLink), flow.Types) : result.Content,
            Existing = true,
            Restored = deleted && !dryRun,
        };
    }
}
