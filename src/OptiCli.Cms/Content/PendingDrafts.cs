using System.Globalization;
using EPiServer.Core;
using EPiServer.DataAbstraction;
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
/// <para>One exception, for Optimizely Commerce 14's catalog content (<see cref="CommerceLanguageCopy"/>): Commerce 14
/// keeps catalog content in every language of its catalog, and when it lists the versions of an item that has none in one
/// of those languages, it saves one itself (<c>CatalogContentVersionStore.EnsureDraftsInAllLanguages</c>): a copy of what
/// it serves in that language, saved as <c>system</c> (its cached copy of the list has no user name), with the time the
/// item was last changed live (by the latest publish in any of its languages), not the time it made the copy. Every
/// translation would otherwise publish "changes saved by system", and confirming them with <c>includeDraft</c> would also
/// wave through a real draft in that language. The copy is left out only while it is what Commerce made: the first
/// version of its branch, not the master language's, never published, saved by <c>system</c> or nobody, within a second
/// of a version of another language saved before it, and holding no value of its language (<see
/// cref="CommerceCopyValues"/>: every culture-specific property the type's default for a new item, empty where it has
/// none, and its <c>SeoUri</c> the one Commerce makes). An editor's save into it records the editor and the time of the
/// save, a later version is never the branch's first, and a draft that gives the language a value of its own differs
/// from the defaults, so none of those is left out. What remains: a first version saved by a job or an import as
/// <c>system</c> or without a user name, within a second of another language's save, whose culture-specific values are
/// all the type's defaults; its shared values are the master language's (Commerce reads them from there), so it
/// carries nothing of that language to put live. Commerce 15 saves no such copies (a language branch of catalog content
/// is made as a CMS one is), so the rule applies only on CMS 12, the only CMS Commerce 14 runs on (<see
/// cref="Compat.CmsApi.CommerceLanguageCopies"/>). (Olav, the Commerce E2E feedback plan, item 2.)</para>
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
    /// Whether a version's culture-specific values are those of Commerce 14's copy of a language (<see
    /// cref="CommerceLanguageCopy"/>). Commerce builds the copy as it loads the item in a language without values: a new
    /// instance of the type with its defaults (the CMS's content factory with <c>SetPropertyValues</c>, so the model's
    /// <c>SetDefaultValues</c> and the defaults set in admin), filled with the values stored in that language, of which
    /// there are none. So each value is the type's default for a new item (empty where it has none), but <c>SeoUri</c>,
    /// which Commerce makes for the language (<c>&lt;name&gt;-sv.aspx</c>, <c>&lt;name&gt;-2-sv.aspx</c>). Anything else, or
    /// a value it can't compare with the default, is a value someone gave, so the version isn't the copy.
    /// </summary>
    /// <param name="cultureSpecific">The version's culture-specific values by property (a block's as <c>Block.Property</c>).</param>
    /// <param name="defaults">The same of a new item of the type in that language; null when it couldn't be made, which only empty values pass.</param>
    /// <param name="language">The version's language (<c>sv</c>).</param>
    public static bool CommerceCopyValues(IEnumerable<KeyValuePair<string, object?>> cultureSpecific, IReadOnlyDictionary<string, object?>? defaults, string language) =>
        cultureSpecific.All(p => string.Equals(p.Key, CommerceSeoUri, StringComparison.OrdinalIgnoreCase)
            ? p.Value is null || p.Value is string uri && uri.Trim().EndsWith($"-{language}.aspx", StringComparison.OrdinalIgnoreCase)
            : Same(p.Value, defaults?.GetValueOrDefault(p.Key)));

    /// <summary>
    /// Whether two values are the same: both empty, the same text, rich text or number, or lists of equal items. Values it
    /// can't compare (a ContentArea with items, an object without value equality) aren't.
    /// </summary>
    private static bool Same(object? value, object? other) => (value, other) switch
    {
        _ when Empty(value) && Empty(other) => true,
        _ when Empty(value) || Empty(other) => false,
        (EPiServer.Core.ContentArea, _) or (_, EPiServer.Core.ContentArea) => false,
        (EPiServer.Core.XhtmlString a, EPiServer.Core.XhtmlString b) => string.Equals(a.ToInternalString(), b.ToInternalString(), StringComparison.Ordinal),
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        (System.Collections.IEnumerable a, System.Collections.IEnumerable b) => a.Cast<object?>().SequenceEqual(b.Cast<object?>()),
        (ValueType or EPiServer.Core.ContentReference, _) => value!.Equals(other),
        _ => false,
    };

    /// <summary>The culture-specific property Commerce fills in its copy of a language: the item's address in it.</summary>
    public const string CommerceSeoUri = "SeoUri";

    /// <summary>No value: null, a blank string, or an empty list (a ContentArea without items, rich text without text).</summary>
    private static bool Empty(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        // Before rich text: a ContentArea is rich text on CMS 12.
        EPiServer.Core.ContentArea area => !area.Items.Any(),
        EPiServer.Core.XhtmlString xhtml => xhtml.IsEmpty || string.IsNullOrWhiteSpace(xhtml.ToInternalString()),
        System.Collections.IEnumerable items => !items.Cast<object?>().Any(),
        _ => false,
    };

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

    /// <summary>
    /// The branch's published version when it is newer than <paramref name="baseVersion"/>, the version a publish would put
    /// live: that publish replaces it, and with it the changes published since <paramref name="baseVersion"/> was saved
    /// (the CMS publishes an older version as it is). Null when there is no published version, or it isn't newer.
    /// </summary>
    /// <param name="branch">Every version of the branch.</param>
    public static ContentVersion? NewerPublished(IEnumerable<ContentVersion> branch, int baseVersion) =>
        branch.FirstOrDefault(v => v.Status == VersionStatus.Published) is { } published && published.ContentLink.WorkID > baseVersion ? published : null;

    /// <summary>Who published <paramref name="published"/>, as the CMS recorded it: whoever set its status, else whoever saved it.</summary>
    public static string? PublishedBy(ContentVersion published) =>
        string.IsNullOrWhiteSpace(published.StatusChangedBy) ? published.SavedBy : published.StatusChangedBy;

    /// <summary>
    /// Whether replacing <paramref name="published"/> needs the caller's confirmation (<c>includeDraft</c>): someone else
    /// published it, or nobody signed in did (a scheduled job or import). The caller's own published version needs none;
    /// the result's warning still says it is replaced.
    /// </summary>
    public static bool PublishedByOthers(ContentVersion published, string self) => !SavedBy(PublishedBy(published), self);

    /// <summary>"version 42_12, published by X (2026-11-01 08:00:00Z)".</summary>
    public static string DescribePublished(ContentVersion published)
    {
        var by = PublishedBy(published);
        var who = string.IsNullOrWhiteSpace(by) ? "without a user name (a scheduled job or import)" : $"by {by.Trim()}";
        return $"version {published.ContentLink}, published {who} ({published.Saved.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)})";
    }

    /// <summary>What publishing <paramref name="version"/> over the newer <paramref name="published"/> does, for a refusal.</summary>
    /// <param name="what">The content, e.g. <c>123 ('News')</c>, <c>123 ('News') in 'en'</c>.</param>
    public static string ReplacesMessage(string what, ContentReference version, ContentVersion published) =>
        $"Publishing version {version} of {what} would replace the published {DescribePublished(published)}, which is newer: the changes published in it would be replaced.";

    /// <summary>The conflict a publish of an older version fails with, unconfirmed, when someone else published the newer one.</summary>
    /// <param name="what">The content, with its language when it has one.</param>
    public static AgentException ReplacesPublished(string what, ContentReference version, ContentVersion published) =>
        new(AgentErrorCodes.Conflict, ReplacesMessage(what, version, published), ReplacesPublishedHint)
        {
            Reason = AgentErrorReasons.NewerPublished,
        };

    /// <summary>The hint of <see cref="ReplacesPublished"/>.</summary>
    public const string ReplacesPublishedHint =
        "Tell the user whose published changes those are and ask whether to replace them; if so, retry with includeDraft. To keep them, save the change again on the published version. Nothing was published.";

    /// <summary>The warning of a publish that replaces (or replaced) a newer published version.</summary>
    /// <param name="replaces">"replaces", "would replace", "will replace", "replaced".</param>
    public static ValidationIssue Replaced(ContentReference version, ContentVersion published, string replaces) => new(null,
        $"Version {version} is older than the published {DescribePublished(published)}: publishing it {replaces} that version, and the changes published in it.",
        "warning")
    {
        Code = ValidationIssueCodes.NewerPublished,
    };

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
