using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using EPiServer.Validation;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Content;

/// <summary>
/// What create, draft and languages share: apply the request to a writable instance, diff it against
/// its starting point, validate, then either report (dry run) or save.
/// </summary>
internal sealed class WriteFlow
{
    public WriteFlow(AgentRequest request)
    {
        Types = request.Service<IContentTypeRepository>();
        Versions = request.Service<IContentVersionRepository>();
        Locator = new ContentLocator(request.Service<IContentRepository>(), Versions, request.Service<ILanguageBranchRepository>());
        Writer = new PropertyWriter(Locator);
        Areas = new AreaEditor(Locator, Writer);
        Validation = request.Service<IValidationService>();
    }

    public ContentLocator Locator { get; }

    public PropertyWriter Writer { get; }

    public AreaEditor Areas { get; }

    public IContentTypeRepository Types { get; }

    public IContentVersionRepository Versions { get; }

    public IContentRepository Repository => Locator.Repository;

    public IValidationService Validation { get; }

    public static SaveAction DraftAction(bool publish) =>
        (publish ? SaveAction.Publish : SaveAction.Save) | SaveAction.ForceNewVersion;

    /// <param name="writable">The instance with the request applied.</param>
    /// <param name="before">Snapshot of the starting point (base version, master language, or type defaults).</param>
    /// <param name="shown">What to report as the content for a dry run or a no-op.</param>
    /// <param name="saveUnchanged">Save even when nothing changed (new content, new branches, publish).</param>
    /// <param name="precheck">Issues found before CMS validation (e.g. the type isn't allowed under the parent).</param>
    public WriteResult Save(
        IContent writable,
        IReadOnlyDictionary<string, JsonElement?> before,
        SaveAction action,
        bool dryRun,
        ContentSummary? shown,
        int? baseVersion,
        bool saveUnchanged,
        IEnumerable<ValidationIssue>? precheck = null)
    {
        var changes = PropertyValues.Diff(before, PropertyValues.Snapshot(writable));
        var issues = (precheck ?? []).Concat(ValidationErrors.Validate(Validation, writable, action)).ToList();
        var valid = !ValidationErrors.HasErrors(issues);
        var published = (action & SaveAction.Publish) == SaveAction.Publish;

        if (dryRun)
        {
            return new WriteResult
            {
                Content = shown,
                DryRun = true,
                Valid = valid,
                BaseVersion = baseVersion,
                Changes = changes,
                Validation = issues.Count > 0 ? issues : null,
            };
        }
        if (!valid)
        {
            throw AgentException.Invalid(issues);
        }
        if (changes.Count == 0 && !saveUnchanged)
        {
            return new WriteResult { Content = shown, BaseVersion = baseVersion, Validation = issues.Count > 0 ? issues : null };
        }

        var saved = Repository.Save(writable, action, AccessLevel.NoAccess);
        if (!published && writable is IVersionable)
        {
            // ForceNewVersion leaves the old primary draft in place, and edit mode would keep opening that one.
            Versions.SetCommonDraft(saved);
        }
        return new WriteResult
        {
            Content = ContentSummaries.Describe(Repository.Get<IContent>(saved), Types),
            Saved = true,
            Published = published,
            BaseVersion = baseVersion,
            Changes = changes,
            Validation = issues.Count > 0 ? issues : null,
        };
    }
}
