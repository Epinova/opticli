using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>One saved version of a language branch, as the pending-draft rule needs it.</summary>
/// <param name="SavedBy">The CMS's "saved by" (<c>ContentVersion.SavedBy</c>, <c>tblWorkContent.ChangedByName</c>).</param>
internal readonly record struct VersionStamp(int Id, bool Published, DateTime Saved, string? SavedBy);

/// <summary>
/// A publish puts the whole version it is based on live, so also the changes saved since the published version that
/// it carries. When someone other than the caller saved one of those, publishing needs the caller's confirmation.
/// </summary>
/// <remarks>
/// The caller is whoever the CMS records the save under (<see cref="CmsCall.UserName"/>): the opticli principal for the
/// developer's agent, so its own earlier drafts never count; the editor for the MCP module, so an editor's own drafts
/// (from the edit UI or an earlier conversation) are theirs to publish, and only a colleague's need confirming.
/// </remarks>
internal static class PendingDrafts
{
    /// <param name="versions">Every version of the branch.</param>
    /// <param name="baseVersion">The version the publish is based on (or publishes).</param>
    /// <param name="self">The caller's user name (<see cref="CmsCall.UserName"/>).</param>
    /// <returns>
    /// The newest version after the published one (every version, when the branch was never published) up to
    /// <paramref name="baseVersion"/> that someone other than <paramref name="self"/> saved; null when there is none.
    /// </returns>
    public static VersionStamp? NewestByOthers(IEnumerable<VersionStamp> versions, int baseVersion, string self) =>
        ByOthers(versions, baseVersion, self).Select(v => (VersionStamp?)v).FirstOrDefault();

    /// <summary>As <see cref="NewestByOthers"/>: every such version, newest first.</summary>
    public static IReadOnlyList<VersionStamp> ByOthers(IEnumerable<VersionStamp> versions, int baseVersion, string self)
    {
        var all = versions.ToList();
        var published = all.Where(v => v.Published).Select(v => v.Id).DefaultIfEmpty(0).Max();
        return all
            .Where(v => v.Id > published && v.Id <= baseVersion && !SavedBy(v.SavedBy, self))
            .OrderByDescending(v => v.Id)
            .ToList();
    }

    /// <summary>
    /// Whether the version a publish is based on carries a draft's changes. A higher version id doesn't say so: a change
    /// based on the published version (or an older one) leaves out the drafts saved after that, and so does everything
    /// based on that change later.
    /// </summary>
    /// <param name="draftChanges">The draft compared with the published version (with nothing, when there is none).</param>
    /// <param name="baseDifferences">The base version compared with the draft.</param>
    /// <returns>True when the base has the draft's value for at least one property the draft changed.</returns>
    public static bool Carries(IReadOnlyList<PropertyChange> draftChanges, IReadOnlyList<PropertyChange> baseDifferences) =>
        draftChanges.Any(c => !baseDifferences.Any(d => d.Property.Equals(c.Property, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Whether <paramref name="self"/> saved a version: the agent's saves are attributed to
    /// <see cref="AgentProtocol.PrincipalName"/>, an editor's to their user name. An empty name is nobody's, not even a
    /// caller without one: a scheduled job or an import saves that way.
    /// </summary>
    public static bool SavedBy(string? savedBy, string self) =>
        !string.IsNullOrWhiteSpace(self) && string.Equals(savedBy?.Trim(), self.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stops a publish that would put <paramref name="pending"/> live unless the caller confirmed it; a dry run only
    /// reports it.
    /// </summary>
    /// <param name="confirmed"><c>includeDraft</c>, or for a publish a version the caller named.</param>
    /// <returns><paramref name="pending"/>, for the result.</returns>
    /// <exception cref="AgentException"><c>conflict</c> with <see cref="AgentException.PendingDraft"/>.</exception>
    public static PendingDraft? Require(PendingDraft? pending, bool confirmed, bool dryRun, string what, string? language) =>
        pending is null || confirmed || dryRun ? pending : throw Unconfirmed(what, language, pending);

    /// <summary>The conflict a publish without confirmation fails with.</summary>
    /// <param name="what">The content, e.g. <c>123 ('News')</c>.</param>
    /// <param name="language">The branch, null for content that isn't localizable.</param>
    public static AgentException Unconfirmed(string what, string? language, PendingDraft draft) => new(
        AgentErrorCodes.Conflict,
        $"Publishing {what}{(language is null ? "" : $" in '{language}'")} would also put live {draft.Describe()}.",
        "Ask whether those changes should go live too; if so, retry with includeDraft. Saving without publish changes nothing live.")
    {
        PendingDraft = draft,
    };
}
