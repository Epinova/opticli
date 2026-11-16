using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Projects;

/// <summary>An item of a project with its version as loaded: null when that version no longer exists.</summary>
internal sealed record LoadedItem(ProjectItem Item, IContent? Version)
{
    public ProjectItemState State => ProjectRules.State((Version as IVersionable)?.Status, Version?.IsDeleted ?? false);
}

/// <summary>
/// What every project operation shares: the CMS's project repository, whether the site's edit UI has projects at all,
/// which project a request names, and how a project and its items are described.
/// </summary>
/// <remarks>
/// A project has no access rights of its own in the CMS (every editor sees every project, as in the edit UI); its items
/// are content, which an editor sees and changes only as far as their access rights go.
/// </remarks>
internal sealed class ProjectContext(CmsCall call)
{
    /// <summary>The category the edit UI gives every project item it adds.</summary>
    public const string ItemCategory = "default";

    public CmsCall Call { get; } = call;

    public ProjectRepository Repository { get; } = call.Service<ProjectRepository>();

    private IContentRepository Content => Call.Service<IContentRepository>();

    private IContentTypeRepository Types => Call.Service<IContentTypeRepository>();

    /// <summary>
    /// Which project UI editors have (<see cref="ProjectEditorUi"/>; null when it can't be told), refusing when they
    /// have none: with project mode and the Projects gadget both off, nobody could review or publish the project in the CMS.
    /// </summary>
    /// <exception cref="AgentException"><c>refused</c> with <see cref="AgentErrorReasons.ProjectsDisabled"/>.</exception>
    public string? RequireEnabled()
    {
        var ui = Compat.CmsProjectUi.Of(Call.Services);
        if (ui == Compat.CmsProjectUi.None)
        {
            throw new AgentException(AgentErrorCodes.Refused,
                "Projects are turned off on this site: its CMS edit UI has neither project mode nor the Projects gadget (EPiServer:CmsUI:ProjectUI ProjectModeEnabled and ProjectGadgetEnabled are both false), so editors couldn't review or publish a project.",
                Call.ForCaller("Save without a project, or turn one of them on in the site's configuration. Nothing was changed.",
                    "Save without a project; the site's developers turn projects on. Nothing was changed."))
            {
                Reason = AgentErrorReasons.ProjectsDisabled,
            };
        }
        return ui;
    }

    /// <summary>Stops a change whose caller has gone, as <see cref="WriteFlow.ThrowIfAborted"/> does a save.</summary>
    public void ThrowIfAborted()
    {
        if (Call.Aborted.IsCancellationRequested)
        {
            throw new AgentException(AgentErrorCodes.Internal, "The caller stopped waiting before the change, so nothing was changed.", "the CLI timed out or was interrupted");
        }
    }

    /// <inheritdoc cref="ProjectLookup.Find"/>
    public Project Resolve(string? reference) => ProjectLookup.Find(Repository.List(), id => Repository.Get(id), reference);

    /// <summary>The project's items, each with its version.</summary>
    public IReadOnlyList<LoadedItem> Items(Project project) => Repository.ListItems(project.ID).Select(Load).ToList();

    /// <summary>The item with its version: the one it names, or (for an item without one) the content's in the item's language.</summary>
    public LoadedItem Load(ProjectItem item)
    {
        IContent? version;
        if (item.ContentLink.WorkID > 0)
        {
            version = Content.TryGet<IContent>(item.ContentLink, out var loaded) && loaded.ContentLink.ID == item.ContentLink.ID ? loaded : null;
        }
        else
        {
            version = Content.TryGet<IContent>(item.ContentLink, item.Language ?? CultureInfo.InvariantCulture, out var loaded) ? loaded : null;
        }
        return new LoadedItem(item, version);
    }

    /// <summary>
    /// Whether the caller sees <paramref name="item"/>: always the developer; an editor an item whose content they can
    /// read, or whose version no longer exists (there is nothing of it to hide).
    /// </summary>
    public bool Visible(LoadedItem item) => item.Version is null || Call.CanRead(item.Version);

    /// <summary>The project as results show it, with its item and ready counts (for an editor, of the items they see).</summary>
    /// <param name="items">Its items, if loaded already.</param>
    public ProjectSummary Describe(Project project, IReadOnlyList<LoadedItem>? items = null)
    {
        items = (items ?? Items(project)).Where(Visible).ToList();
        return new ProjectSummary
        {
            Id = project.ID,
            Name = project.Name,
            Status = ProjectRules.StatusName(project.Status),
            ScheduledFor = project.Status == ProjectStatus.DelayedPublished ? project.DelayPublishUntil?.ToUniversalTime() : null,
            Created = project.Created == DateTime.MinValue ? null : project.Created.ToUniversalTime(),
            CreatedBy = string.IsNullOrEmpty(project.CreatedBy) ? null : project.CreatedBy,
            Items = items.Count,
            Ready = items.Count(i => i.State == ProjectItemState.Ready),
        };
    }

    /// <summary>An item as results show it: its version, else (a version that no longer exists) what the item names.</summary>
    public ContentSummary Summary(LoadedItem item) => item.Version is { } version
        ? ContentSummaries.Describe(version, Types)
        : new ContentSummary
        {
            Ref = item.Item.ContentLink.ToString(),
            Id = item.Item.ContentLink.ID,
            Version = item.Item.ContentLink.WorkID > 0 ? item.Item.ContentLink.WorkID : null,
            Guid = Guid.Empty,
            Name = "",
            Language = LanguageName(item.Item.Language),
        };

    /// <summary>The item for messages: <c>123 ('News') in 'en'</c>, or <c>123_456</c> for a version that no longer exists.</summary>
    public static string What(LoadedItem item) => item.Version is { } version
        ? What(version)
        : $"{item.Item.ContentLink}{In(LanguageName(item.Item.Language))}";

    /// <summary>Content for messages: <c>123 ('News') in 'en'</c>.</summary>
    public static string What(IContent content) =>
        $"{content.ContentLink.ID.ToString(CultureInfo.InvariantCulture)} ('{content.Name}'){In(LanguageName(Language(content)))}";

    private static string In(string? language) => string.IsNullOrEmpty(language) ? "" : $" in '{language}'";

    /// <summary>
    /// The language a project item of <paramref name="content"/> has, as the edit UI gives it: the content's language, or
    /// the invariant culture for content that isn't localizable.
    /// </summary>
    public static CultureInfo Language(IContent content) =>
        content is ILocalizable { Language: { } language } ? language : CultureInfo.InvariantCulture;

    /// <summary>Whether two item languages are the same; an item without one counts as the invariant culture.</summary>
    public static bool SameLanguage(CultureInfo? a, CultureInfo? b) =>
        string.Equals(a?.Name ?? "", b?.Name ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>A language's code; null for the invariant culture (content that isn't localizable).</summary>
    public static string? LanguageName(CultureInfo? language) => string.IsNullOrEmpty(language?.Name) ? null : language.Name;

    /// <summary>
    /// The items of <paramref name="content"/> (any version) in its language in projects other than
    /// <paramref name="except"/> that aren't published, with their project: content the edit UI won't put in another project.
    /// </summary>
    public IReadOnlyList<(ProjectItem Item, Project Project)> InOtherProjects(ContentReference content, CultureInfo language, int except) =>
        Repository.GetItems([content.ToReferenceWithoutVersion()])
            .Where(i => i.ProjectID != except && SameLanguage(i.Language, language))
            .Select(i => (Item: i, Project: Repository.Get(i.ProjectID)))
            .Where(p => p.Project is not null && p.Project.Status != ProjectStatus.Published)
            .Select(p => (p.Item, p.Project!))
            .ToList();
}
