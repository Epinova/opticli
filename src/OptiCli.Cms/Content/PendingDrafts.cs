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
/// <para>A project publish also leaves out the versions of whoever marked the item Ready to publish (<see cref="ReadyBy"/>):
/// marking it ready was their consent to publish it, their own earlier drafts in it included (Olav, after phase 3 of the
/// projects plan). The CMS records a check-in on the version itself: <c>editSetVersionStatus</c> sets both
/// <c>tblWorkContent.ChangedByName</c> (<c>ContentVersion.SavedBy</c>) and <c>NewStatusByName</c>
/// (<c>ContentVersion.StatusChangedBy</c>) to the user who checks it in, on CMS 12 and 13 alike, while the older versions
/// keep the names of those who saved them. So who marked the version ready is known, and so is who saved each older draft
/// it carries; who wrote the ready version's own changes is lost once someone else checks it in, which is why marking
/// another person's draft ready needs <c>includeDraft</c> itself (<see cref="Operations.ProjectReadyOperation"/>).</para>
/// <para>One exception, for Optimizely Commerce's catalog content (<see cref="CommerceLanguageCopy"/>): Commerce 14 keeps
/// catalog content in every language of its catalog, and when it lists the versions of an item that has none in one of
/// those languages, it saves one itself (<c>CatalogContentVersionStore.EnsureDraftsInAllLanguages</c>): a copy of what it
/// serves in that language, saved as <c>system</c> (its cached copy of the list has no user name), with the time the
/// item was last changed live (by the latest publish in any of its languages), not the time it made the copy. Every
/// translation would otherwise publish "changes saved by system", and confirming them with <c>includeDraft</c> would also
/// wave through a real draft in that language. The copy is left out only while it is what Commerce made: the first
/// version of its branch, not the master language's, never published, saved by <c>system</c> or nobody, within a second
/// of a version of another language saved before it. An editor's save into it records the editor and the time of the
/// save, and a later draft, also a job's, is a version of its own, so neither is ever left out (Olav, the Commerce E2E
/// feedback plan, item 2). Commerce 15 saves no such copies: a language branch of catalog content is made as a CMS one
/// is.</para>
/// </remarks>
internal static class PendingDrafts
{
    /// <summary>The user Commerce 14 saves its own copy of catalog content in another language as (<see cref="CommerceLanguageCopy"/>).</summary>
    public const string CommerceSystemUser = "system";

    /// <summary>
    /// The name of Commerce's content provider, as catalog content's refs have it (the Commerce protocol's
    /// <c>CatalogRefSyntax.Provider</c>, which the editors' module doesn't compile in).
    /// </summary>
    public const string CommerceProvider = "CatalogContent";

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
    /// <param name="readyBy">
    /// Who marked <paramref name="baseVersion"/> Ready to publish, for a project publish (<see cref="ReadyBy"/>): their
    /// versions are left out too.
    /// </param>
    public static IReadOnlyList<VersionStamp> ByOthers(IEnumerable<VersionStamp> versions, int baseVersion, string self, string? readyBy = null)
    {
        var all = versions.ToList();
        var published = all.Where(v => v.Published).Select(v => v.Id).DefaultIfEmpty(0).Max();
        return all
            .Where(v => v.Id > published && v.Id <= baseVersion && !SavedBy(v.SavedBy, self) && (readyBy is null || !SavedBy(v.SavedBy, readyBy)))
            .OrderByDescending(v => v.Id)
            .ToList();
    }

    /// <summary>
    /// Whether a version of catalog content in a language other than its master language is Commerce 14's own copy of
    /// that language, which holds nobody's changes (see the remarks): the first version of its branch, never published,
    /// saved by <see cref="CommerceSystemUser"/> or nobody, within <see cref="CommerceCopyTime"/> of a version of another
    /// language saved before it (the publish that last changed the item, whose time Commerce gave the copy).
    /// </summary>
    /// <param name="version">A version of the branch, from <see cref="ByOthers"/>.</param>
    /// <param name="branch">Every version of its branch.</param>
    /// <param name="others">Every version of the content's other languages.</param>
    public static bool CommerceLanguageCopy(VersionStamp version, IEnumerable<VersionStamp> branch, IEnumerable<VersionStamp> others) =>
        !version.Published
        && (string.IsNullOrWhiteSpace(version.SavedBy) || string.Equals(version.SavedBy.Trim(), CommerceSystemUser, StringComparison.Ordinal))
        && branch.All(v => v.Id >= version.Id)
        && others.Any(o => o.Id < version.Id && (o.Saved.ToUniversalTime() - version.Saved.ToUniversalTime()).Duration() <= CommerceCopyTime);

    /// <summary>
    /// How far the time Commerce gives its copy (the item's last modified time, set by the latest publish in any language)
    /// may be from that publish's version: the two are written apart, a few milliseconds.
    /// </summary>
    public static readonly TimeSpan CommerceCopyTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Who consented to publishing a project item's version by marking it Ready to publish: the user the CMS recorded
    /// checking it in (<c>ContentVersion.StatusChangedBy</c>), when the version is ready (<c>CheckedIn</c>) and that isn't the
    /// caller. Null otherwise, also when nobody was signed in (a job or an import).
    /// </summary>
    /// <param name="checkedIn">The version's status is <c>CheckedIn</c> (Ready to publish, or approved by its reviewers).</param>
    /// <param name="statusChangedBy">Who changed its status last, as the CMS recorded it.</param>
    /// <param name="self">The caller's user name (<see cref="CmsCall.UserName"/>).</param>
    public static string? ReadyBy(bool checkedIn, string? statusChangedBy, string self) =>
        checkedIn && !string.IsNullOrWhiteSpace(statusChangedBy) && !SavedBy(statusChangedBy, self) ? statusChangedBy.Trim() : null;

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
