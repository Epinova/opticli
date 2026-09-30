using System.Globalization;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

internal static class CreateEndpoint
{
    public static WriteResult Handle(AgentRequest request, CreateRequest body)
    {
        var flow = new WriteFlow(request);
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            throw AgentException.Usage("name is required.");
        }

        var type = TypeEndpoint.Find(flow.Types, body.Type);
        var (parent, owner) = Parent(request, flow, body.Parent, body.ForContent, body.DryRun);
        var culture = body.Lang is { } lang ? flow.Locator.EnabledLanguage(lang) : MasterLanguage(owner);

        if (body.Guid is { } guid && ExistingContent.Find(flow, guid) is { } existing)
        {
            return ExistingContent.Update(flow, existing, body.UpdateExisting, type, parent, body.Lang is null ? null : culture, body.Name, body.Properties, body.Publish, body.DryRun);
        }

        var content = culture is null
            ? flow.Repository.GetDefault<IContent>(parent.ContentLink, type.ID)
            : flow.Repository.GetDefault<IContent>(parent.ContentLink, type.ID, culture);
        if (body.Guid is { } fixedGuid)
        {
            content.ContentGuid = fixedGuid;
        }
        var before = PropertyValues.Snapshot(content);
        content.Name = body.Name;
        flow.Writer.Apply(content, body.Properties);

        return flow.Save(
            content,
            before,
            body.Publish ? SaveAction.Publish : SaveAction.Save,
            body.DryRun,
            shown: null,
            baseVersion: null,
            saveUnchanged: true,
            precheck: Availability(request, parent, type, flow.Types, ParentType(flow, body)));
    }

    /// <summary>
    /// The explicit parent, or the "For this page" assets folder of <c>forContent</c>; plus the content
    /// whose master language new content defaults to (the parent, or the folder's owner).
    /// </summary>
    internal static (IContent Parent, IContent LanguageSource) Parent(AgentRequest request, WriteFlow flow, string? parentRef, string? forContent, bool dryRun)
    {
        if ((parentRef is null) == (forContent is null))
        {
            throw AgentException.Usage("Give exactly one of parent or forContent.");
        }
        if (parentRef is not null)
        {
            var parent = flow.Locator.LoadAnyLanguage(flow.Locator.ResolveContent(parentRef, "parent"));
            return (parent, parent);
        }

        var owner = flow.Locator.LoadAnyLanguage(flow.Locator.ResolveContent(forContent, "forContent"));
        var assets = request.Service<ContentAssetHelper>();
        // A dry run must not create the folder; validating against the owner is close enough.
        IContent folder = dryRun
            ? (IContent?)assets.GetAssetFolder(owner.ContentLink) ?? owner
            : assets.GetOrCreateAssetFolder(owner.ContentLink);
        return (folder, owner);
    }

    private static CultureInfo? MasterLanguage(IContent content) =>
        content is ILocalizable { MasterLanguage: { } master } ? master : null;

    /// <summary>The editor's "allowed types" rule for pages, reported as a validation error rather than an exception.</summary>
    private static ContentType? ParentType(WriteFlow flow, CreateRequest body) =>
        body.ParentType is null ? null
        : body.DryRun ? TypeEndpoint.Find(flow.Types, body.ParentType)
        : throw AgentException.Usage("parentType only applies to a dry run.");

    /// <param name="plannedParent">The type the real parent will have, when the dry run uses a stand-in parent.</param>
    private static IEnumerable<ValidationIssue> Availability(AgentRequest request, IContent parent, ContentType type, IContentTypeRepository types, ContentType? plannedParent = null)
    {
        if (type is not PageType || (plannedParent is null ? parent is not PageData : plannedParent is not PageType))
        {
            yield break;
        }
        var parentType = plannedParent ?? types.Load(parent.ContentTypeID);
        if (parentType is not null && !request.Service<ContentTypeAvailabilityService>().IsAllowed(parentType.Name, type.Name))
        {
            yield return new ValidationIssue(null, $"{type.Name} is not allowed below {parentType.Name} ({parent.ContentLink.ID}).");
        }
    }
}
