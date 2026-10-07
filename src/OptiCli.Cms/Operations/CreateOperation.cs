using System.Globalization;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>New content of any type but media (the agent's <c>POST /v1/content</c>); with a known GUID, see <see cref="ExistingContent"/>.</summary>
internal static class CreateOperation
{
    public static WriteResult Run(CmsCall call, CreateRequest body)
    {
        var flow = new WriteFlow(call);
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            throw AgentException.Usage("name is required.");
        }

        var type = TypeOperation.Find(call, flow.Types, body.Type);
        if (Kind(type) == PlacementKind.Media)
        {
            throw AgentException.Usage($"{type.Name} is a media type; created this way it would be a media item without a file.",
                call.ForCaller($"Upload the file instead (POST {AgentRoutes.Media}, opticli upload), which picks or checks the media type.",
                    "Upload the file instead (upload_media), which picks or checks the media type."));
        }
        var (parent, owner) = Parent(flow, body.Parent, body.ForContent, body.DryRun);
        var culture = body.Lang is { } lang ? flow.Locator.EnabledLanguage(lang) : MasterLanguage(owner);

        if (body.Guid is { } guid && ExistingContent.Find(flow, guid) is { } existing)
        {
            return ExistingContent.Update(flow, existing, body.UpdateExisting, type, parent, body.Lang is null ? null : culture, body.Name, body.Properties, body.Publish, body.RequestApproval, body.IncludeDraft, body.DryRun, body.PublishAt, body.Composition);
        }

        // CMS 13: from a Visual Builder blueprint, which must be of the type asked for.
        var blueprint = body.Blueprint is { } blueprintId ? Compat.CmsCompositionWrites.Blueprint(flow, blueprintId, culture) : null;
        if (blueprint is not null && blueprint.ContentTypeID != type.ID)
        {
            throw AgentException.Usage($"The blueprint '{blueprint.Name}' is {flow.Types.Load(blueprint.ContentTypeID)?.Name}, not {type.Name}.", "Leave type to the blueprint's, or pick another blueprint.");
        }

        var content = culture is null
            ? flow.Repository.GetDefault<IContent>(parent.ContentLink, type.ID)
            : flow.Repository.GetDefault<IContent>(parent.ContentLink, type.ID, culture);
        if (body.Guid is { } fixedGuid)
        {
            content.ContentGuid = fixedGuid;
        }
        var before = PropertyValues.Snapshot(content);
        if (blueprint is not null)
        {
            Compat.CmsCompositionWrites.CopyBlueprint(flow, blueprint, content);
        }
        content.Name = body.Name;
        flow.Writer.Apply(content, body.Properties);
        Compat.CmsCompositionWrites.Apply(flow, content, body.Composition, null);
        // For a plan's dry run under a stand-in parent, that is the nearest existing ancestor, whose sequence is inherited.
        var action = WriteFlow.Publishing(call, parent.ContentLink, body.Publish, body.RequestApproval, body.PublishAt, $"New {type.Name} '{body.Name}'");
        WriteFlow.ScheduleAt(content, action, body.PublishAt);

        return flow.Save(
            content,
            before,
            action ?? SaveAction.Save,
            body.DryRun,
            shown: null,
            baseVersion: null,
            saveUnchanged: true,
            precheck: Availability(call, parent, body.ForContent is not null, type, flow.Types, ParentType(flow, body)));
    }

    /// <summary>
    /// The explicit parent, or the "For this page" assets folder of <c>forContent</c>; plus the content
    /// whose master language new content defaults to (the parent, or the folder's owner).
    /// </summary>
    internal static (IContent Parent, IContent LanguageSource) Parent(WriteFlow flow, string? parentRef, string? forContent, bool dryRun)
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
        var assets = flow.Call.Service<ContentAssetHelper>();
        if (!dryRun)
        {
            // The CMS creates the folder unchecked; for an editor, adding to it is a change to content they must be able to edit.
            flow.Call.RequireAccess(owner, AccessLevel.Edit);
        }
        // A dry run must not create the folder; validating against the owner is close enough.
        IContent folder = dryRun
            ? (IContent?)assets.GetAssetFolder(owner.ContentLink) ?? owner
            : assets.GetOrCreateAssetFolder(owner.ContentLink);
        return (folder, owner);
    }

    private static CultureInfo? MasterLanguage(IContent content) =>
        content is ILocalizable { MasterLanguage: { } master } ? master : null;

    /// <summary>The type the real parent will have, for a dry run under a stand-in parent (see <see cref="Availability"/>).</summary>
    private static ContentType? ParentType(WriteFlow flow, CreateRequest body) =>
        body.ParentType is null ? null
        : body.DryRun ? TypeOperation.Find(flow.Call, flow.Types, body.ParentType)
        : throw AgentException.Usage("parentType only applies to a dry run.");

    /// <summary>
    /// Whether new content of <paramref name="type"/> may go below <paramref name="parent"/> (<see cref="Placement"/>),
    /// reported as a validation error rather than an exception, so a dry run lists it with the rest.
    /// </summary>
    /// <param name="assetsFolder">The parent is a "For this page" folder; in a dry run it may be its owner, as the folder doesn't exist yet.</param>
    /// <param name="plannedParent">The type the real parent will have, when the dry run uses a stand-in parent.</param>
    internal static IReadOnlyList<ValidationIssue> Availability(CmsCall call, IContent parent, bool assetsFolder, ContentType type, IContentTypeRepository types, ContentType? plannedParent = null)
    {
        var parentType = plannedParent
            ?? (assetsFolder && parent is not ContentFolder ? types.Load(typeof(ContentAssetFolder)) : types.Load(parent.ContentTypeID));
        return parentType is null ? [] : Placement(call, type, parentType, parent.ContentLink.ID, creating: true, parent);
    }

    /// <summary>
    /// Where content of <paramref name="type"/> may go (<see cref="ContentPlacement"/>), with the parent type's availability
    /// as the CMS's <see cref="ContentTypeAvailabilityService"/> answers it, for create, upload and move alike; for new
    /// content (<paramref name="creating"/>) also whether the caller may create the type at all (<see cref="CmsCall.MayCreate"/>).
    /// </summary>
    /// <param name="parent">The parent itself, for the access its type groups require on it (<see cref="CmsCall.MayCreate"/>).</param>
    internal static IReadOnlyList<ValidationIssue> Placement(CmsCall call, ContentType type, ContentType parentType, int parentId, bool creating, IContent? parent = null)
    {
        var availability = call.Service<ContentTypeAvailabilityService>();
        var allowed = availability.IsAllowed(parentType.Name, type.Name);
        if (ContentPlacement.Problem(Kind(type), type.Name, Kind(parentType), parentType.Name, parentId, allowed) is { } problem)
        {
            return [new ValidationIssue(null, problem)];
        }
        // A move isn't a create: the type's access rights only say who may create its content, as in the CMS.
        return creating && !call.MayCreate(type, parentType, parent)
            ? [new ValidationIssue(null, $"You may not create {type.Name} content here: the content type's access rights, or those of its group of types, don't allow it.")]
            : [];
    }

    internal static PlacementKind Kind(ContentType type) => type switch
    {
        PageType => PlacementKind.Page,
        _ when Compat.CmsApi.IsBlockType(type) => PlacementKind.Block,
        { ModelType: { } model } when typeof(MediaData).IsAssignableFrom(model) => PlacementKind.Media,
        { ModelType: { } model } when typeof(ContentFolder).IsAssignableFrom(model) => PlacementKind.Folder,
        _ => PlacementKind.Other,
    };
}
