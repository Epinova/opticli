using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// Whether a change goes into an existing draft (<see cref="DraftRequest.IntoVersion"/>) instead of a new version, and
/// the save action that does it.
/// </summary>
/// <remarks>
/// <para>What the CMS does with it (CMS 12 and 13 alike): from a <c>CheckedOut</c> version, <c>Save</c>,
/// <c>Schedule</c> and <c>RequestApproval</c>, with <c>ForceCurrentVersion</c> or without a versioning flag, keep the
/// version id (<c>DefaultStatusTransitionEvaluator</c>), and the save writes the version's data with the caller as its
/// "saved by" and now as its saved time (<c>editSaveContentVersionData</c>), as an edit-UI save does. From a published,
/// previously published, scheduled or awaiting version the same actions would overwrite that version or make a new
/// one, which is why only a <c>CheckedOut</c> one is used.</para>
/// <para>A publish is <c>Publish</c> without a flag: it too keeps a <c>CheckedOut</c> version's id, and, as with
/// <c>ForceNewVersion</c> and the edit UI's own publish, makes the published version the primary draft
/// (<c>CommonDraftController</c>: <c>SetForced</c>), which <c>Publish | ForceCurrentVersion</c> doesn't.</para>
/// </remarks>
internal static class SaveInto
{
    /// <param name="intoVersion">The version the request names; null: none.</param>
    /// <param name="latest">The latest version of the branch (the variation's, for a variation): the one the concurrency check takes.</param>
    /// <param name="self">The caller's user name (<see cref="CmsCall.UserName"/>).</param>
    /// <param name="startsVariation">CMS 13: the change makes the variation's first version, from the content's.</param>
    /// <param name="basedElsewhere">The request has a <c>from</c>, or its ref names a version.</param>
    /// <returns>True when the change is saved into <paramref name="intoVersion"/>; false when it gets a new version.</returns>
    public static bool Applies(int? intoVersion, ContentVersion latest, string self, bool startsVariation, bool basedElsewhere) =>
        intoVersion is { } version
        && !startsVariation
        && !basedElsewhere
        && latest.ContentLink.WorkID == version
        && latest.Status == VersionStatus.CheckedOut
        && PendingDrafts.SavedBy(latest.SavedBy, self);

    /// <summary>
    /// The CMS refused to save into the version because the site's own code asked for a new one: a handler of the saving
    /// events that sets <c>SaveContentEventArgs.RequireNewVersion</c> adds <c>ForceNewVersion</c>, and together with
    /// <c>ForceCurrentVersion</c> <c>DefaultContentRepository.Save</c> throws (<c>SaveActionExtentions.ThrowIfInvalid</c>:
    /// an <c>InvalidOperationException</c>, in CMS 13's save pipeline an <c>EPiServerException</c>) before anything is saved.
    /// </summary>
    public static bool NewVersionRequired(SaveAction action, Exception ex)
    {
        if (!action.HasFlag(SaveAction.ForceCurrentVersion))
        {
            return false;
        }
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("Cannot force new version and force current version", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The error for <see cref="NewVersionRequired"/>: only <c>apply</c> asks to save into a draft, and its <c>--version-per-step</c> avoids it.</summary>
    public static AgentException NewVersionRequiredError(int version) => AgentException.Conflict(
        $"The site's own code asks for a new version on this save (a handler of the CMS's saving events sets RequireNewVersion), so the change can't go into draft {version}, where `apply` saves later operations on the same content and language. Nothing was saved.",
        "Run the plan with `opticli apply --version-per-step`, which saves a version per operation; check first what the operations before this one saved (their results and undo hints).");

    /// <param name="action">From <see cref="WriteFlow.Publishing"/>: null saves a draft.</param>
    public static SaveAction Action(SaveAction? action) => action switch
    {
        SaveAction.Publish => SaveAction.Publish,
        { } other => other | SaveAction.ForceCurrentVersion,
        null => SaveAction.Save | SaveAction.ForceCurrentVersion,
    };
}
