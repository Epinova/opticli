using System.Globalization;
using System.Text.Json;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Web;
using EPiServer.Web.Routing;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>Describes a loaded content version as <see cref="ContentItem"/>.</summary>
/// <param name="shown">Which properties to describe (see <see cref="ReadValues"/>); every one when null.</param>
internal sealed class ContentReader(IContentTypeRepository types, IContentVersionRepository versions, IUrlResolver urls, Func<IContentData, PropertyData, bool>? shown = null)
{
    public ContentItem Describe(IContent content)
    {
        var localizable = content as ILocalizable;
        var versionable = content as IVersionable;
        var tracked = content as IChangeTrackable;
        var language = (content as ILocale)?.Language;
        var link = content.ContentLink;

        return new ContentItem
        {
            Ref = link.ToReferenceWithoutVersion().ToString(),
            Id = link.ID,
            Version = link.WorkID > 0 ? link.WorkID : PrimaryVersion(link, versionable, language),
            Guid = content.ContentGuid,
            Name = content.Name,
            Type = types.Load(content.ContentTypeID)?.Name,
            Kind = Kind(content),
            Language = Code(language),
            MasterLanguage = Code(localizable?.MasterLanguage),
            Languages = localizable?.ExistingLanguages.Select(Code).OfType<string>().Order(StringComparer.Ordinal).ToList(),
            Status = versionable is null ? null : JsonNamingPolicy.CamelCase.ConvertName(versionable.Status.ToString()),
            Parent = ContentReference.IsNullOrEmpty(content.ParentLink) ? null : content.ParentLink.ToReferenceWithoutVersion().ToString(),
            Deleted = content.IsDeleted ? true : null,
            StartPublish = versionable?.StartPublish,
            StopPublish = versionable?.StopPublish,
            Saved = tracked?.Saved,
            ChangedBy = tracked?.ChangedBy,
            Url = Url(link, language),
            Properties = new ReadValues(types, shown).Properties(content),
        };
    }

    /// <summary>
    /// The version a reference without one loads: the published version, or for content that was never
    /// published the common draft. The loaded instance itself doesn't carry it.
    /// </summary>
    private int? PrimaryVersion(ContentReference link, IVersionable? versionable, CultureInfo? language)
    {
        var code = Code(language);
        var version = code is null ? versions.LoadPublished(link) : versions.LoadPublished(link, code);
        if (version is null && versionable is { Status: not VersionStatus.Published })
        {
            version = versions.LoadCommonDraft(link, code ?? "");
        }
        return version?.ContentLink.WorkID is > 0 and var id ? id : null;
    }

    private static string Kind(IContent content) => content switch
    {
        PageData => "page",
        BlockData => "block",
        MediaData => "media",
        ContentFolder => "folder",
        _ => "other",
    };

    private static string? Code(CultureInfo? culture) =>
        culture is null || culture.Equals(CultureInfo.InvariantCulture) ? null : culture.Name;

    /// <summary>The public URL, as templates would link to it (never the edit or preview URL).</summary>
    private string? Url(ContentReference link, CultureInfo? language)
    {
        var url = urls.GetUrl(
            link.ToReferenceWithoutVersion(),
            Code(language),
            new UrlResolverArguments { ContextMode = ContextMode.Default, ForceCanonical = true });
        return string.IsNullOrEmpty(url) ? null : url;
    }
}
