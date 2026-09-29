using OptiCli.Core.Cms;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Refs;
using OptiCli.Core.Urls;

namespace OptiCli.Core.Content;

/// <param name="VersionId">Set when the ref named a version (<c>123_456</c>).</param>
/// <param name="Url">Set when the ref was a URL: how it resolved, including the language it selects.</param>
public sealed record LocatedContent(int Id, int? VersionId, ResolvedUrl? Url);

/// <summary>Turns any <see cref="ContentRef"/> into a content id that exists.</summary>
public sealed class ContentLocator(CmsDatabase db, CmsModel model)
{
    /// <exception cref="NotFoundException">Nothing matches the ref.</exception>
    public async Task<LocatedContent> LocateAsync(ContentRef reference, SiteInfo? site, CancellationToken cancellationToken)
    {
        switch (reference.Kind)
        {
            case ContentRefKind.Id:
                if (await ContentHeaderReader.ByIdAsync(db, reference.Id, cancellationToken) is null)
                {
                    throw new NotFoundException($"No content with id {reference.Id}.", "Find content with `opticli search <text>` or `opticli tree <ref>`.");
                }
                return new LocatedContent(reference.Id, reference.VersionId, null);

            case ContentRefKind.Guid:
                var ids = await ContentHeaderReader.IdsByGuidsAsync(db, [reference.Guid], cancellationToken);
                return ids.TryGetValue(reference.Guid, out var id)
                    ? new LocatedContent(id, null, null)
                    : throw new NotFoundException($"No content with GUID {reference.Guid:D}.", "GUIDs are stable across environments, but the item may not exist in this database.");

            default:
                var resolved = await new UrlResolver(db, model).ResolveAsync(reference.Url!, site, cancellationToken);
                return new LocatedContent(resolved.ContentId, null, resolved);
        }
    }
}
