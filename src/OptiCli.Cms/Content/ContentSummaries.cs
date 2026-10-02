using System.Text.Json;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

internal static class ContentSummaries
{
    public static ContentSummary Describe(IContent content, IContentTypeRepository types) => new()
    {
        Ref = content.ContentLink.ToString(),
        Id = content.ContentLink.ID,
        Version = content.ContentLink.WorkID > 0 ? content.ContentLink.WorkID : null,
        Guid = content.ContentGuid,
        Name = content.Name,
        Type = types.Load(content.ContentTypeID)?.Name,
        Language = (content as ILocalizable)?.Language?.Name,
        Status = content is IVersionable versionable ? JsonNamingPolicy.CamelCase.ConvertName(versionable.Status.ToString()) : null,
        Parent = ContentReference.IsNullOrEmpty(content.ParentLink) ? null : content.ParentLink.ToReferenceWithoutVersion().ToString(),
    };
}
