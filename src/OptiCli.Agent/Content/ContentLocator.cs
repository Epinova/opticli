using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Agent.Http;

namespace OptiCli.Agent.Content;

/// <summary>Turns refs and language codes from a request into CMS references, with agent errors when they don't exist.</summary>
internal sealed class ContentLocator(IContentRepository repository, IContentVersionRepository versions, ILanguageBranchRepository languages)
{
    public IContentRepository Repository { get; } = repository;

    /// <summary>Resolves a ref, keeping its version if it has one.</summary>
    public ContentReference Resolve(string? reference, string what = "ref")
    {
        if (!RefSyntax.TryParse(reference, out var parsed))
        {
            throw AgentException.Usage($"'{reference}' is not a valid {what}: expected {RefSyntax.Description}.",
                "URLs and paths are resolved by the CLI; send the id.");
        }
        if (parsed.Guid is { } guid)
        {
            return Repository.TryGet<IContent>(guid, out var byGuid)
                ? byGuid.ContentLink.ToReferenceWithoutVersion()
                : throw AgentException.NotFound($"No content with GUID {guid}.");
        }

        if (parsed.Provider is { } provider)
        {
            var provided = new ContentReference(parsed.Id, 0, provider);
            return Repository.TryGet<IContent>(provided, AnyLanguage(), out var content)
                ? content.ContentLink.ToReferenceWithoutVersion()
                : throw AgentException.NotFound($"No content {reference?.Trim()} from the content provider '{provider}'.",
                    "The id before __ is local to each database; the content's GUID works across environments.");
        }

        var link = new ContentReference(parsed.Id, parsed.Version ?? 0);
        if (!Repository.TryGet<IContent>(link.ToReferenceWithoutVersion(), AnyLanguage(), out _))
        {
            throw AgentException.NotFound($"No content with id {parsed.Id}.");
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
    public IContent LoadAnyLanguage(ContentReference link) =>
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
        var branch = languages.ListEnabled().FirstOrDefault(l => l.LanguageID.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
        return branch?.Culture ?? throw AgentException.Usage(
            $"'{code}' is not an enabled language on this site.",
            $"Enabled: {string.Join(", ", languages.ListEnabled().Select(l => l.LanguageID))}.");
    }

    private static LoaderOptions AnyLanguage() => new() { LanguageLoaderOption.FallbackWithMaster() };

    /// <summary>
    /// The most recently created version (highest version id) in <paramref name="language"/>: the one
    /// <c>baseVersion</c> is checked against and a draft is based on.
    /// </summary>
    public ContentVersion LatestVersion(ContentReference link, CultureInfo? language)
    {
        var all = versions.List(link.ToReferenceWithoutVersion());
        var inLanguage = language is null
            ? all
            : all.Where(v => string.Equals(v.LanguageBranch, language.Name, StringComparison.OrdinalIgnoreCase));
        return inLanguage.OrderByDescending(v => v.ContentLink.WorkID).FirstOrDefault()
            ?? throw AgentException.NotFound($"Content {link.ID} has no versions{(language is null ? "" : $" in '{language.Name}'")}.");
    }
}
