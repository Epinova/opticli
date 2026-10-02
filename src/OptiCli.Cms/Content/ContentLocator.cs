using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>Turns refs and language codes from a request into CMS references, with agent errors when they don't exist.</summary>
/// <remarks>
/// Every ref an operation is given is resolved here, so this is also where an editor's Read access is checked: content
/// they can't read is reported exactly as content that doesn't exist (<see cref="CmsCall.RequireRead{T}"/>).
/// </remarks>
internal sealed class ContentLocator(CmsCall call)
{
    private readonly IContentVersionRepository _versions = call.Service<IContentVersionRepository>();

    private readonly ILanguageBranchRepository _languages = call.Service<ILanguageBranchRepository>();

    public IContentRepository Repository { get; } = call.Service<IContentRepository>();

    /// <summary>Resolves a ref, keeping its version if it has one.</summary>
    public ContentReference Resolve(string? reference, string what = "ref")
    {
        if (!RefSyntax.TryParse(reference, out var parsed))
        {
            throw AgentException.Usage($"'{reference}' is not a valid {what}: expected {RefSyntax.Description}.",
                // The CLI resolves URLs and paths itself; an editor's assistant has a tool for it.
                call.Caller == CmsCaller.Editor ? "Resolve a URL or path to its id first." : "URLs and paths are resolved by the CLI; send the id.");
        }
        if (parsed.Guid is { } guid)
        {
            return Repository.TryGet<IContent>(guid, out var byGuid) && call.CanRead(byGuid)
                ? byGuid.ContentLink.ToReferenceWithoutVersion()
                : throw AgentException.NotFound($"No content with GUID {guid}.");
        }

        if (parsed.Provider is { } provider)
        {
            var provided = new ContentReference(parsed.Id, 0, provider);
            return Repository.TryGet<IContent>(provided, AnyLanguage(), out var content) && call.CanRead(content)
                ? content.ContentLink.ToReferenceWithoutVersion()
                : throw AgentException.NotFound($"No content {reference?.Trim()} from the content provider '{provider}'.",
                    "The id before __ is local to each database; the content's GUID works across environments.");
        }

        var link = new ContentReference(parsed.Id, parsed.Version ?? 0);
        if (!Repository.TryGet<IContent>(link.ToReferenceWithoutVersion(), AnyLanguage(), out var byId) || !call.CanRead(byId))
        {
            throw CmsCall.NotFound(link);
        }
        return link;
    }

    /// <summary>Resolves a ref that must name content, not one of its versions.</summary>
    public ContentReference ResolveContent(string? reference, string what = "ref")
    {
        var link = Resolve(reference, what);
        if (link.WorkID > 0)
        {
            throw AgentException.Usage($"'{reference}' is a version; this operation applies to the whole content item. Use '{link.ID}'.");
        }
        return link;
    }

    /// <summary>The content in any language, for identity and structure checks.</summary>
    /// <exception cref="AgentException"><c>not_found</c> for content the caller can't read.</exception>
    public IContent LoadAnyLanguage(ContentReference link) =>
        call.RequireRead(Repository.Get<IContent>(link.ToReferenceWithoutVersion(), AnyLanguage()));

    /// <summary>
    /// <see cref="LoadAnyLanguage"/> without the read check, for content the caller has read already or isn't shown: what
    /// an operation just changed (a move may take it where the caller can no longer read it: the recycle bin, or below
    /// other access rights), and the ancestors whose structure it needs.
    /// </summary>
    public IContent LoadUnchecked(ContentReference link) =>
        Repository.Get<IContent>(link.ToReferenceWithoutVersion(), AnyLanguage());

    /// <summary>
    /// The language to work in: the requested one (which the content must have), else the content's master language.
    /// Null for content that isn't localizable (folders, media).
    /// </summary>
    public CultureInfo? ContentLanguage(IContent content, string? requested)
    {
        if (content is not ILocalizable localizable)
        {
            return requested is null
                ? null
                : throw AgentException.Usage($"Content {content.ContentLink.ID} is not localizable; leave out lang.");
        }
        if (requested is null)
        {
            return localizable.MasterLanguage;
        }
        var culture = EnabledLanguage(requested);
        if (!localizable.ExistingLanguages.Any(l => l.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw AgentException.NotFound(
                $"Content {content.ContentLink.ID} has no '{culture.Name}' language branch (it has {string.Join(", ", localizable.ExistingLanguages.Select(l => l.Name))}).",
                "Create the branch first (POST .../languages, opticli translate).");
        }
        return culture;
    }

    /// <exception cref="AgentException">The language is unknown or not enabled on the site.</exception>
    public CultureInfo EnabledLanguage(string code)
    {
        var branch = _languages.ListEnabled().FirstOrDefault(l => l.LanguageID.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
        return branch?.Culture ?? throw AgentException.Usage(
            $"'{code}' is not an enabled language on this site.",
            $"Enabled: {string.Join(", ", _languages.ListEnabled().Select(l => l.LanguageID))}.");
    }

    private static LoaderOptions AnyLanguage() => new() { LanguageLoaderOption.FallbackWithMaster() };

    /// <summary>Every version in <paramref name="language"/> (all of them for content that isn't localizable), newest first.</summary>
    public IReadOnlyList<ContentVersion> Versions(ContentReference link, CultureInfo? language)
    {
        var all = _versions.List(link.ToReferenceWithoutVersion());
        var inLanguage = language is null
            ? all
            : all.Where(v => string.Equals(v.LanguageBranch, language.Name, StringComparison.OrdinalIgnoreCase));
        return inLanguage.OrderByDescending(v => v.ContentLink.WorkID).ToList();
    }

    /// <summary>
    /// The most recently created version (highest version id) in <paramref name="language"/>: the one
    /// <c>baseVersion</c> is checked against and a draft is based on.
    /// </summary>
    public ContentVersion LatestVersion(ContentReference link, CultureInfo? language) => Latest(Versions(link, language), link, language);

    /// <param name="branch">The versions of one branch, newest first (<see cref="Versions"/>).</param>
    public static ContentVersion Latest(IReadOnlyList<ContentVersion> branch, ContentReference link, CultureInfo? language) =>
        branch.FirstOrDefault()
        ?? throw AgentException.NotFound($"Content {link.ID} has no versions{(language is null ? "" : $" in '{language.Name}'")}.");

    /// <summary>The branch's published version id; null when it has never been published (or isn't now).</summary>
    public static int? PublishedVersion(IReadOnlyList<ContentVersion> branch) =>
        branch.FirstOrDefault(v => v.Status == VersionStatus.Published)?.ContentLink.WorkID;

    /// <summary>
    /// What publishing <paramref name="based"/> would put live besides the request's own change: the versions of its
    /// branch after the published one, up to <paramref name="based"/>, that someone other than the caller saved and whose
    /// changes <paramref name="based"/> carries (<see cref="PendingDrafts.Carries"/>).
    /// </summary>
    /// <param name="branch">The versions of <paramref name="based"/>'s branch (<see cref="Versions"/>).</param>
    /// <param name="based">The version the publish is based on, as loaded.</param>
    /// <returns>The newest such version; null when there is none (or the content isn't versioned).</returns>
    public PendingDraft? PendingDraft(IReadOnlyList<ContentVersion> branch, IContent based)
    {
        if (based is not IVersionable)
        {
            return null;
        }
        var stamps = branch.Select(v => new VersionStamp(v.ContentLink.WorkID, v.Status == VersionStatus.Published, v.Saved, v.SavedBy));
        var candidates = PendingDrafts.ByOthers(stamps, based.ContentLink.WorkID, call.UserName);
        if (candidates.Count == 0)
        {
            return null;
        }
        var published = PublishedVersion(branch) is { } publishedId
            ? PropertyValues.Snapshot(Repository.Get<IContent>(new ContentReference(based.ContentLink.ID, publishedId)))
            : new Dictionary<string, System.Text.Json.JsonElement?>();
        var basedValues = PropertyValues.Snapshot(based);
        foreach (var candidate in candidates)
        {
            var draft = candidate.Id == based.ContentLink.WorkID
                ? basedValues
                : PropertyValues.Snapshot(Repository.Get<IContent>(new ContentReference(based.ContentLink.ID, candidate.Id)));
            if (PendingDrafts.Carries(PropertyValues.Diff(published, draft), PropertyValues.Diff(draft, basedValues)))
            {
                return new PendingDraft(
                    $"{based.ContentLink.ID.ToString(CultureInfo.InvariantCulture)}_{candidate.Id.ToString(CultureInfo.InvariantCulture)}",
                    candidate.SavedBy,
                    candidate.Saved.ToUniversalTime(),
                    PropertyValues.Diff(published, basedValues));
            }
        }
        return null;
    }
}
