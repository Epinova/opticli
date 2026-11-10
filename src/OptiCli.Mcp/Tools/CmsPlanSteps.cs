using System.Text.Json;
using System.Text.Json.Nodes;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// The steps of a site's write tool's plan, against the CMS, as the editor: each through the operation the module's own
/// tool runs for the same change (<c>update_content</c>'s, <c>create_content</c>'s, <c>move_content</c>'s,
/// <c>delete_content</c>'s), never publishing; their dry runs, plus the access rights the CMS only checks on save.
/// </summary>
/// <remarks>
/// A plan stops at its first failed step, with the steps before it saved. So a check refuses up front what the save would
/// refuse for the editor's access rights: Edit on content an update changes, Create below a new item's parent (Edit on
/// the content whose "For this page" folder it goes in), Delete on content a move or trash takes away and Create where a
/// move puts it. A trash has no dry run of its own in the operations: <see cref="CheckTrash"/> is it.
/// </remarks>
internal sealed class CmsPlanSteps(IHttpContextAccessor http, IOptions<OptiCliMcpOptions> options) : IContentPlanSteps
{
    /// <summary>What a trash step's dry run says about <c>referencedBy</c>, so an empty list isn't taken for "nothing refers to it".</summary>
    internal const string ReferencedByNote = "Lists content that refers to it in its published version; references from unpublished drafts aren't included.";

    /// <summary>The most descendants of trashed content that are loaded to count those the editor can read.</summary>
    internal const int MaxCountedDescendants = 1000;

    /// <summary>The most items that refer to trashed content which are loaded, and listed.</summary>
    internal const int MaxCheckedReferences = 100, MaxListedReferences = 20;

    private CmsCall? _call;

    private CmsCall Call => _call ??= McpEditor.From(http).Call(options.Value);

    private HttpContext Context => http.HttpContext!;

    public StepOutcome Check(PlanStep step) => step switch
    {
        UpdateStep update => CheckUpdate(update),
        CreateStep create => CheckCreate(create),
        MoveStep move => CheckMove(move),
        TrashStep trash => CheckTrash(trash),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.Kind, "No such step."),
    };

    public StepOutcome Run(PlanStep step, JsonObject? facts)
    {
        switch (step)
        {
            case UpdateStep update:
            {
                var baseVersion = facts?["baseVersion"] is JsonValue version ? version.GetValue<int>() : (int?)null;
                var result = DraftOperation.Run(Call, Reference(update.Content), Draft(update, baseVersion, dryRun: false));
                return new StepOutcome(Json(ResultUrls.Serialize(result, result.Content, Context, preview: result.Saved)))
                {
                    Status = result.Saved ? "saved" : "unchanged",
                    Content = Link(result.Content) ?? update.Content,
                };
            }
            case CreateStep create:
            {
                var result = CreateOperation.Run(Call, Create(create, dryRun: false));
                return new StepOutcome(Json(ResultUrls.Serialize(result, result.Content, Context, preview: result.Saved)))
                {
                    Status = "saved",
                    Content = Link(result.Content) ?? throw new InvalidOperationException("The CMS saved new content without a reference."),
                };
            }
            case MoveStep move:
            {
                var result = MoveOperation.Run(Call, Reference(move.Content), new MoveRequest { Parent = Reference(move.Destination.Existing!) });
                return new StepOutcome(Json(ResultUrls.Serialize(result, result.Content, Context, wholeContent: true))) { Status = "moved", Content = move.Content.ToReferenceWithoutVersion() };
            }
            case TrashStep trash:
            {
                ToolGates.Deleting(options.Value);
                var result = DeleteOperation.Run(Call, Reference(trash.Content));
                return new StepOutcome(Json(ResultUrls.Serialize(result, result.Content, Context, wholeContent: true))) { Status = "trashed", Content = trash.Content.ToReferenceWithoutVersion() };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(step), step.Kind, "No such step.");
        }
    }

    /// <summary>
    /// <c>update_content</c>'s dry run, based on the version the tool planned from, and Edit access. The version it is
    /// based on is what the plan token covers: a newer save changes it.
    /// </summary>
    /// <remarks>
    /// Content loaded without a version (<c>IContentLoader.Get</c>, <see cref="IOptiCliEditor.Load{T}"/> without
    /// <c>latest</c>) is the version such a load gives: the published one, or for content never published its primary
    /// draft (as <c>get_content</c> reads it). A newer draft, anyone's, makes the step a <c>conflict</c>: the tool's values
    /// were worked out without it, and would replace what that draft changed.
    /// </remarks>
    private StepOutcome CheckUpdate(UpdateStep update)
    {
        var call = Call;
        var content = new WriteFlow(call).Locator.LoadAnyLanguage(update.Content);
        DraftsOnly(content);
        var baseVersion = update.Content.WorkID > 0 ? update.Content.WorkID : PrimaryVersion(call, update.Content, update.Language, content);
        WriteResult result;
        try
        {
            result = DraftOperation.Run(call, Reference(update.Content), Draft(update, baseVersion, dryRun: true));
        }
        catch (AgentException e) when (e.Code == AgentErrorCodes.Conflict && e.CurrentVersion is not null && update.Content.WorkID == 0)
        {
            throw new AgentException(AgentErrorCodes.Conflict,
                $"{e.Message} The tool planned from version {baseVersion}, the one visitors see (or, for content never published, its primary draft).",
                "There is a newer draft of it (it may be the user's own, or one this tool saved before), and this change would overwrite it. Read the content's latest version (get_content with version: \"latest\") and tell the user what that draft holds: it must be published or discarded in the CMS before this tool can change the content; then run the dry run again. Nothing was changed.")
            {
                CurrentVersion = e.CurrentVersion,
            };
        }
        call.RequireAccess(content, AccessLevel.Edit);
        return new StepOutcome(Json(ResultUrls.Serialize(result, result.Content, Context)))
        {
            Valid = result.Valid,
            Facts = new JsonObject { ["baseVersion"] = result.BaseVersion },
        };
    }

    /// <summary>
    /// <c>create_content</c>'s dry run, and Create below the parent (Edit on the content whose "For this page" folder it
    /// goes in). Below planned content, it runs below the existing content that will be made below, with the planned
    /// content's type for "allowed below" (<see cref="CreateRequest.ParentType"/>).
    /// </summary>
    private StepOutcome CheckCreate(CreateStep create)
    {
        var call = Call;
        var result = CreateOperation.Run(call, Create(create, dryRun: true));
        var flow = new WriteFlow(call);
        var parent = flow.Locator.LoadAnyLanguage(create.Parent.Existing!);
        var type = TypeOperation.Find(call, flow.Types, create.Type);
        if (CreateOperation.Kind(type) == PlacementKind.Other)
        {
            // Pages, blocks and media have versions, and folders show visitors nothing; content of another kind may not.
            DraftsOnly(flow.Repository.GetDefault<IContent>(parent.ContentLink, type.ID));
        }
        call.RequireAccess(parent, create.InAssets ? AccessLevel.Edit : AccessLevel.Create);
        return new StepOutcome(Json(ResultUrls.Serialize(result, result.Content, Context)))
        {
            Valid = result.Valid,
            Facts = new JsonObject { ["siblingsOfType"] = SiblingsOfType(call, create, parent, type) },
        };
    }

    /// <summary>
    /// How many items of <paramref name="type"/> the new content's parent has now (all of them, whatever the editor may
    /// read: the count only goes into the plan token's hash). Once the step has run there is one more, so the token of
    /// the dry run no longer fits: a retry with it (after a client gave up on the call, say) is refused rather than
    /// creating everything again.
    /// </summary>
    private static int SiblingsOfType(CmsCall call, CreateStep create, IContent parent, ContentType type)
    {
        var folder = create.InAssets ? call.Service<ContentAssetHelper>().GetAssetFolder(parent.ContentLink)?.ContentLink : parent.ContentLink;
        return ContentReference.IsNullOrEmpty(folder)
            ? 0
            : call.Service<IContentLoader>().GetChildren<IContent>(folder.ToReferenceWithoutVersion(), new LoaderOptions { LanguageLoaderOption.MasterLanguage() })
                .Count(c => c.ContentTypeID == type.ID);
    }

    /// <summary>
    /// The version a reference without one loads, for <paramref name="language"/>: the published one, or for content
    /// never published its primary draft (<c>get_content</c>'s rule). None for content without versions.
    /// </summary>
    private static int? PrimaryVersion(CmsCall call, ContentReference link, string? language, IContent content)
    {
        if (content is not IVersionable)
        {
            return null;
        }
        var versions = call.Service<IContentVersionRepository>();
        var plain = link.ToReferenceWithoutVersion();
        var version = (language is null ? versions.LoadPublished(plain) : versions.LoadPublished(plain, language))
            ?? versions.LoadCommonDraft(plain, language ?? "");
        return version?.ContentLink.WorkID is > 0 and var id ? id : null;
    }

    public IReadOnlySet<int> AncestorIds(ContentReference link) =>
        Call.Service<IContentLoader>().GetAncestors(link.ToReferenceWithoutVersion()).Select(a => a.ContentLink.ID).ToHashSet();

    /// <summary>
    /// <c>move_content</c>'s dry run, Delete on the content and Create below the destination. To planned content, it runs
    /// to the existing content that will be made below, with the planned content's type for "allowed below".
    /// </summary>
    private StepOutcome CheckMove(MoveStep move)
    {
        var call = Call;
        var flow = new WriteFlow(call);
        var plannedType = move.PlannedDestinationType is { } name ? TypeOperation.Find(call, flow.Types, name) : null;
        var result = MoveOperation.Run(call, Reference(move.Content), new MoveRequest { Parent = Reference(move.Destination.Existing!), DryRun = true }, plannedType);
        call.RequireAccess(flow.Locator.LoadAnyLanguage(move.Content), AccessLevel.Delete);
        call.RequireAccess(flow.Locator.LoadAnyLanguage(move.Destination.Existing!), move.DestinationInAssets ? AccessLevel.Edit : AccessLevel.Create);
        return new StepOutcome(Json(ResultUrls.Serialize(result, result.Content, Context, wholeContent: true)))
        {
            Facts = new JsonObject { ["previousParent"] = result.PreviousParent },
        };
    }

    /// <summary>
    /// What <c>delete_content</c> would refuse, without moving anything: content the editor can't read, a site root or
    /// start page (or above one), content already in the recycle bin, no Delete access, the site's delete gate. And what
    /// goes with it: how many items below it, of those the editor can read, go too, and which content the editor can read
    /// refers to it.
    /// </summary>
    internal StepOutcome CheckTrash(TrashStep trash)
    {
        ToolGates.Deleting(options.Value);
        var call = Call;
        var flow = new WriteFlow(call);
        var link = flow.Locator.ResolveContent(Reference(trash.Content));
        var content = MoveOperation.Movable(flow, link);
        if (content.IsDeleted)
        {
            throw AgentException.Conflict($"Content {link.ID} is already in the recycle bin.");
        }
        call.RequireAccess(content, AccessLevel.Delete);

        var loader = call.Service<IContentLoader>();
        var master = new LoaderOptions { LanguageLoaderOption.MasterLanguage() };
        var below = loader.GetDescendents(link).ToList();
        var descendants = loader.GetItems(below.Take(MaxCountedDescendants), master).Count(call.CanRead);

        var referencedBy = new List<ContentSummary>();
        var moreReferences = false;
        var owners = call.Service<IContentRepository>().GetReferencesToContent(link, false)
            .Select(r => r.OwnerID.ToReferenceWithoutVersion())
            .Where(o => !ContentReference.IsNullOrEmpty(o) && o.ID != link.ID)
            .DistinctBy(o => o.ID)
            .Take(MaxCheckedReferences);
        foreach (var owner in owners)
        {
            if (loader.TryGet(owner, master, out IContent found) && call.CanRead(found))
            {
                if (referencedBy.Count == MaxListedReferences)
                {
                    moreReferences = true;
                    break;
                }
                referencedBy.Add(ContentSummaries.Describe(found, flow.Types));
            }
        }

        var summary = ContentSummaries.Describe(content, flow.Types);
        var report = new TrashReport
        {
            Content = summary,
            Parent = content.ParentLink.ToReferenceWithoutVersion().ToString(),
            Descendants = descendants,
            MoreDescendants = below.Count > MaxCountedDescendants ? true : null,
            ReferencedBy = referencedBy,
            MoreReferences = moreReferences ? true : null,
            ReferencedByNote = ReferencedByNote,
        };
        return new StepOutcome(Json(ResultUrls.Serialize(report, summary, Context, wholeContent: true)))
        {
            Facts = new JsonObject { ["descendants"] = descendants, ["moreDescendants"] = below.Count > MaxCountedDescendants },
        };
    }

    /// <summary>
    /// A plan saves drafts only: content without versions (other than a folder, which shows visitors nothing) is live as
    /// soon as it is saved, whatever the save's action, so no plan saves it.
    /// </summary>
    /// <exception cref="AgentException"><c>refused</c>.</exception>
    private static void DraftsOnly(IContent content)
    {
        if (content is not (IVersionable or ContentFolder))
        {
            var what = ContentReference.IsNullOrEmpty(content.ContentLink) ? "New content of this type" : $"Content {content.ContentLink.ID} ('{content.Name}')";
            throw AgentException.Refused(
                $"{what} has no versions: saving it would put the change live at once, and a write tool's plan saves drafts only.",
                "Tell the user to make this change in the CMS edit UI. Nothing was changed.");
        }
    }

    /// <summary>A trash step's dry run.</summary>
    private sealed record TrashReport
    {
        public required ContentSummary Content { get; init; }

        public required string Parent { get; init; }

        /// <summary>The items below it that go too, of those the editor can read (of the first <see cref="MaxCountedDescendants"/>).</summary>
        public required int Descendants { get; init; }

        public bool? MoreDescendants { get; init; }

        /// <summary>Content the editor can read that refers to it (a link, a ContentArea, a reference property): at most <see cref="MaxListedReferences"/>.</summary>
        public required IReadOnlyList<ContentSummary> ReferencedBy { get; init; }

        public bool? MoreReferences { get; init; }

        public required string ReferencedByNote { get; init; }
    }

    private static DraftRequest Draft(UpdateStep update, int? baseVersion, bool dryRun) => new()
    {
        Lang = update.Language,
        Name = update.Name,
        Properties = update.Properties.Count > 0 ? PlanValues.Elements(update.Properties) : null,
        AreaOps = update.AreaOps.Count > 0
            ? update.AreaOps.Select(a => new AreaOperation
            {
                Op = a.Op,
                Property = a.Property,
                Ref = Reference(a.Block.Existing!),
                At = a.At,
                DisplayOption = a.DisplayOption,
                IfMissing = a.IfMissing,
            }).ToList()
            : null,
        BaseVersion = baseVersion,
        DryRun = dryRun,
    };

    private static CreateRequest Create(CreateStep create, bool dryRun) => new()
    {
        Type = create.Type,
        Name = create.Name,
        Parent = create.InAssets ? null : Reference(create.Parent.Existing!),
        ForContent = create.InAssets ? Reference(create.Parent.Existing!) : null,
        Lang = create.Language,
        Properties = create.Properties.Count > 0 ? PlanValues.Elements(create.Properties) : null,
        ParentType = dryRun ? create.PlannedParentType : null,
        DryRun = dryRun,
    };

    /// <summary>The content, as the operations take it: the version is the step's to say (an update's base version).</summary>
    private static string Reference(ContentReference link) => link.ToReferenceWithoutVersion().ToString();

    private static ContentReference? Link(ContentSummary? content) =>
        content is not null && ContentReference.TryParse(content.Ref, out var link) ? link : null;

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();
}
