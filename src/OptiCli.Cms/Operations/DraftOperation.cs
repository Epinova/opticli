using System.Globalization;
using System.Text.Json;
using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Changes existing content (the agent's <c>POST /v1/content/{ref}/draft</c>): a new draft from the latest version, or
/// from the given one, optionally published, scheduled or sent for review.
/// </summary>
internal static class DraftOperation
{
    public static WriteResult Run(CmsCall call, string reference, DraftRequest body)
    {
        var flow = new WriteFlow(call);
        var link = flow.Locator.Resolve(reference);
        var language = flow.Locator.ContentLanguage(flow.Locator.LoadAnyLanguage(link), body.Lang);
        // CMS 13: the content variation asked for, or the one the ref's version belongs to. Its versions are a branch of
        // their own; a variation without any yet is made from the content's published (else latest) version.
        var variation = body.Variation is { } key ? VariationKey(key) : link.WorkID > 0 ? Compat.CmsApi.Variation(flow.Locator.Version(link, link.WorkID)) : null;

        // Optimistic concurrency: the caller must have seen the latest version.
        var branch = flow.Locator.Versions(link, language, variation);
        var own = variation is null ? branch : flow.Locator.Versions(link, language);
        var startsVariation = variation is not null && branch.Count == 0;
        var latest = startsVariation
            ? own.FirstOrDefault(v => v.Status == VersionStatus.Published) ?? ContentLocator.Latest(own, link, language)
            : ContentLocator.Latest(branch, link, language);
        if (body.BaseVersion is { } expected && !startsVariation && expected != latest.ContentLink.WorkID)
        {
            throw new AgentException(
                AgentErrorCodes.Conflict,
                $"Version {expected} is not the latest version of {link.ID}{In(language)}{Of(variation)}; {latest.ContentLink} is ({JsonNamingPolicy.CamelCase.ConvertName(latest.Status.ToString())}, saved {latest.Saved.ToUniversalTime():u} by {latest.SavedBy}).",
                $"Re-read {latest.ContentLink}, reapply the change and retry with baseVersion {latest.ContentLink.WorkID}.")
            {
                CurrentVersion = latest.ContentLink.WorkID,
            };
        }

        var baseLink = BaseLink(flow.Locator, link, body.From, startsVariation ? own : branch, latest, language);
        var current = flow.Locator.Version(link, baseLink.WorkID);
        call.RequireRead(current);
        if (Compat.CmsApi.Variation(current) is { } belongs && !belongs.Equals(variation, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentException.Usage($"Version {baseLink} belongs to the content variation '{belongs}'{(variation is null ? "" : $", not to '{variation}'")}.",
                $"Give variation {belongs} to change it, or a version of {(variation is null ? "the content itself" : $"'{variation}'")}.");
        }
        if (language is not null && current is ILocalizable { Language: { } versionLanguage } && !versionLanguage.Name.Equals(language.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentException.Usage($"Version {baseLink} is in '{versionLanguage.Name}', not '{language.Name}'.",
                body.From is null ? null : $"Give lang {versionLanguage.Name}, or a version of the '{language.Name}' branch.");
        }

        var what = $"{link.ID} ('{current.Name}')";
        Approvals.RequireNotInReview(branch, what, language?.Name);
        var action = WriteFlow.Publishing(call, link, body.Publish, body.RequestApproval, body.PublishAt, what);

        // Read before saving: the publish changes which version is published.
        var pending = action is SaveAction.Publish or SaveAction.Schedule
            ? PendingDrafts.Require(flow.Locator.PendingDraft(branch, current), body.IncludeDraft, body.DryRun, what, language?.Name)
            : null;
        var published = ContentLocator.PublishedVersion(branch);
        var leftOut = LeftOut(branch, baseLink);

        var before = PropertyValues.Snapshot(current);
        var writable = (IContent)((IReadOnly)current).CreateWritableClone();
        if (variation is not null)
        {
            Compat.CmsApi.SetVariation(writable, variation);
        }
        if (body.Name is { } name)
        {
            writable.Name = name;
        }
        flow.Writer.Apply(writable, body.Properties);
        flow.Areas.Apply(writable, body.AreaOps);
        Compat.CmsCompositionWrites.Apply(flow, writable, body.Composition, body.CompositionOps);
        WriteFlow.ScheduleAt(writable, action, body.PublishAt);
        var cleared = WriteFlow.ClearExpiredStopPublish(writable, action, body.Properties);

        var result = flow.Save(
            writable,
            before,
            WriteFlow.DraftAction(action),
            body.DryRun,
            ContentSummaries.Describe(current, flow.Types),
            baseLink.WorkID,
            // Publishing a draft (or sending it for review) saves it even unchanged; an unchanged published version stays as it is.
            saveUnchanged: action is not null && current is IVersionable { Status: not VersionStatus.Published },
            precheck: cleared is null ? null : [cleared]);
        return result with
        {
            PendingDraft = pending,
            PreviouslyPublished = result is { Saved: true, Published: true } ? published : null,
            LeftOut = (result.Saved || result.DryRun) && leftOut.Count > 0 ? leftOut : null,
        };
    }

    /// <summary>The version the change is based on: <paramref name="from"/>, else the ref's own version, else the latest.</summary>
    /// <exception cref="AgentException">
    /// <c>usage</c> for a ref with a version and a from, or a malformed from; <c>notFound</c> for the published version of
    /// a branch that has none, or a version the content doesn't have.
    /// </exception>
    private static ContentReference BaseLink(ContentLocator locator, ContentReference link, string? from, IReadOnlyList<ContentVersion> branch, ContentVersion latest, CultureInfo? language)
    {
        if (from is null)
        {
            return link.WorkID > 0 ? link : latest.ContentLink;
        }
        if (link.WorkID > 0)
        {
            throw AgentException.Usage($"The ref names version {link.WorkID} and from says '{from}'; give one of them.",
                "A ref with a version bases the change on that version, which must be the latest; from bases it on any version.");
        }
        if (from.Trim().Equals(DraftRequest.FromPublished, StringComparison.OrdinalIgnoreCase))
        {
            return ContentLocator.PublishedVersion(branch) is { } publishedId
                ? new ContentReference(link.ID, publishedId)
                : throw AgentException.NotFound($"Content {link.ID} has no published version{In(language)} to base the change on.",
                    "Leave out from to base it on the latest version, or give a version id.");
        }
        if (!int.TryParse(from.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
        {
            throw AgentException.Usage($"from '{from}' is neither '{DraftRequest.FromPublished}' nor a version id.");
        }
        // Any language: a version of another branch is reported as such once loaded.
        return locator.AllVersions(link, null).Any(v => v.ContentLink.WorkID == version)
            ? new ContentReference(link.ID, version)
            : throw AgentException.NotFound($"Content {link.ID} has no version {version}.", $"`opticli versions {link.ID}` lists them.");
    }

    /// <summary>The versions of the branch saved after <paramref name="baseLink"/>, newest first: the change doesn't have theirs.</summary>
    private static List<LeftOutVersion> LeftOut(IReadOnlyList<ContentVersion> branch, ContentReference baseLink) => branch
        .Where(v => v.ContentLink.WorkID > baseLink.WorkID)
        .Select(v => new LeftOutVersion(
            $"{v.ContentLink.ID.ToString(CultureInfo.InvariantCulture)}_{v.ContentLink.WorkID.ToString(CultureInfo.InvariantCulture)}",
            JsonNamingPolicy.CamelCase.ConvertName(v.Status.ToString()),
            v.SavedBy,
            v.Saved.ToUniversalTime(),
            v.IsCommonDraft))
        .ToList();

    private static string In(CultureInfo? language) => language is null ? "" : $" in '{language.Name}'";

    private static string Of(string? variation) => variation is null ? "" : $" in the content variation '{variation}'";

    /// <summary>A content variation's key as the CMS takes it: a letter, then letters, digits and underscores.</summary>
    private static string VariationKey(string key)
    {
        var trimmed = key.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[A-Za-z][_0-9A-Za-z]*$")
            ? trimmed
            : throw AgentException.Usage($"'{key}' is not a content variation key: the CMS takes a letter, then letters, digits and underscores.",
                "`opticli versions <ref>` lists the content's variations.");
    }
}
