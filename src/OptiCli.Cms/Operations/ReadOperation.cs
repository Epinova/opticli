using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Web.Routing;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>What to read: a language branch and a version, both optional.</summary>
/// <param name="Lang">The branch; the master language when left out.</param>
/// <param name="Version"><c>published</c> (the default), <c>latest</c> or a version id.</param>
internal sealed record ReadRequest(string? Lang = null, string? Version = null);

/// <summary>
/// The CMS's own view of one content version (the agent's <c>GET /v1/content/{ref}</c>), so the CLI's DB decoding can
/// be checked against it. Read-only by construction: only loaders and the URL resolver are touched.
/// </summary>
internal static class ReadOperation
{
    public static ContentItem Run(CmsCall call, string reference, ReadRequest body)
    {
        var lang = Blank(body.Lang);
        var version = Blank(body.Version);

        var loader = call.Service<IContentLoader>();
        var versions = call.Service<IContentVersionRepository>();
        var locator = new ContentLocator(call);
        var link = locator.Resolve(reference);
        if (link.WorkID > 0 && version is not null)
        {
            throw AgentException.Usage("Pass either a 123_456 ref or version, not both.");
        }

        var language = Language(locator.LoadAnyLanguage(link), lang);
        var versionLink = link.WorkID > 0 ? link : SelectVersion(locator, link, language, version);
        var content = call.RequireRead(versionLink is null ? Load(loader, link, language) : LoadVersion(loader, versions, versionLink));

        // An editor gets the properties the CMS edit UI shows them, as editable.
        return new ContentReader(call.Service<IContentTypeRepository>(), versions, call.Service<IUrlResolver>(), call.Properties.Shown).Describe(content);
    }

    /// <summary>Null for the primary version; otherwise the version to load.</summary>
    private static ContentReference? SelectVersion(ContentLocator locator, ContentReference link, CultureInfo? language, string? version)
    {
        if (version is null || version.Equals(ReadQuery.Published, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (version.Equals(ReadQuery.Latest, StringComparison.OrdinalIgnoreCase))
        {
            return locator.LatestVersion(link, language).ContentLink;
        }
        return int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? new ContentReference(link.ID, id)
            : throw AgentException.Usage($"Invalid version '{version}'.", "Use published, latest or a version id.");
    }

    /// <summary>
    /// The requested branch, else the master language. Reading a branch in a language the site has since
    /// disabled is fine, unlike writing one. Some content types (settings, for instance) are stored per
    /// language without their model implementing <see cref="ILocalizable"/>; the requested language is passed on
    /// to the loader as it is (the CMS then decides what to load). Null leaves the choice to the loader.
    /// </summary>
    private static CultureInfo? Language(IContent content, string? requested)
    {
        if (content is not ILocalizable localizable)
        {
            return requested is null ? null : Culture(requested);
        }
        if (requested is null)
        {
            return localizable.MasterLanguage;
        }
        return localizable.ExistingLanguages.FirstOrDefault(l => l.Name.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? throw AgentException.NotFound(
                $"Content {content.ContentLink.ID} has no '{requested}' language branch (it has {string.Join(", ", localizable.ExistingLanguages.Select(l => l.Name))}).");
    }

    private static IContent Load(IContentLoader loader, ContentReference link, CultureInfo? language) =>
        language is null
            ? loader.Get<IContent>(link)
            : loader.Get<IContent>(link, new LoaderOptions { LanguageLoaderOption.Specific(language) });

    /// <summary>A version, in its own language (a version belongs to exactly one branch).</summary>
    private static IContent LoadVersion(IContentLoader loader, IContentVersionRepository versions, ContentReference versionLink)
    {
        var version = versions.Load(versionLink);
        if (version is null || version.ContentLink.ID != versionLink.ID)
        {
            throw AgentException.NotFound($"Content {versionLink.ID} has no version {versionLink.WorkID}.");
        }
        var language = string.IsNullOrEmpty(version.LanguageBranch) ? null : Culture(version.LanguageBranch);
        return Load(loader, versionLink, language);
    }

    private static CultureInfo Culture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            throw AgentException.Usage($"'{code}' is not a language code.");
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
