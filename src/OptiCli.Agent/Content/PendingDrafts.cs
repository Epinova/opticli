using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Content;

/// <summary>One saved version of a language branch, as the pending-draft rule needs it.</summary>
/// <param name="SavedBy">The CMS's "saved by" (<c>ContentVersion.SavedBy</c>, <c>tblWorkContent.ChangedByName</c>).</param>
internal readonly record struct VersionStamp(int Id, bool Published, DateTime Saved, string? SavedBy);

/// <summary>
/// A publish puts the whole version it is based on live, so also every change saved since the published version.
/// When someone other than opticli saved one of those, publishing needs the caller's confirmation.
/// </summary>
internal static class PendingDrafts
{
    /// <param name="versions">Every version of the branch.</param>
    /// <param name="baseVersion">The version the publish is based on (or publishes).</param>
    /// <returns>
    /// The newest version after the published one (every version, when the branch was never published) up to
    /// <paramref name="baseVersion"/> that someone other than opticli saved; null when there is none.
    /// </returns>
    public static VersionStamp? NewestByOthers(IEnumerable<VersionStamp> versions, int baseVersion)
    {
        var all = versions.ToList();
        var published = all.Where(v => v.Published).Select(v => v.Id).DefaultIfEmpty(0).Max();
        return all
            .Where(v => v.Id > published && v.Id <= baseVersion && !SavedByOptiCli(v.SavedBy))
            .OrderByDescending(v => v.Id)
            .Select(v => (VersionStamp?)v)
            .FirstOrDefault();
    }

    /// <summary>Saves through the agent are attributed to <see cref="AgentProtocol.PrincipalName"/>; an empty name is nobody's.</summary>
    public static bool SavedByOptiCli(string? savedBy) =>
        string.Equals(savedBy?.Trim(), AgentProtocol.PrincipalName, StringComparison.OrdinalIgnoreCase);

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
