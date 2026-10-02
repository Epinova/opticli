using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OptiCli.Protocol;

namespace OptiCli.Cms;

/// <summary>Who a content operation runs for, which decides how the CMS checks it.</summary>
internal enum CmsCaller
{
    /// <summary>
    /// The developer's local agent (<c>opticli serve</c>): writes pass <see cref="AccessLevel.NoAccess"/>, as the CMS's
    /// own import and migration code does, and every read is allowed. Only reachable on a Development site, from this
    /// machine, with the run's token.
    /// </summary>
    Developer,

    /// <summary>
    /// A signed-in editor (the MCP module): every read and write is checked against the editor's own access rights, so
    /// they can do no more through opticli than in the edit UI.
    /// </summary>
    Editor,
}

/// <summary>
/// One content operation: the request's services, its cancellation, and who it runs for. The only context an operation
/// gets, so the agent and the MCP module run the same code, and neither HTTP nor MCP is visible from it.
/// </summary>
/// <remarks>
/// Every access decision that differs between the callers is made here. An operation never passes an
/// <see cref="AccessLevel"/> to the CMS itself: it saves, moves and deletes through this class, and checks what the CMS
/// doesn't check (reads through <see cref="RequireRead{T}"/>, access rights through <see cref="RequireAccess"/>).
/// </remarks>
/// <param name="publishing">
/// Runs before every save that publishes or schedules a publish, and throws to stop it: the MCP module's gate for what
/// the site and the connection allow (<c>AllowPublish</c>, the <c>content:publish</c> scope). The module also checks
/// the gate up front from a tool's arguments; this is the backstop for any path that would publish without saying so in
/// its arguments. Null lets every publish through, as for the developer.
/// </param>
internal sealed class CmsCall(IServiceProvider services, CancellationToken aborted, CmsCaller caller, Action? publishing = null)
{
    /// <summary>Signalled when the caller gave up (a timeout or Ctrl+C in the CLI): nothing is saved after that.</summary>
    public CancellationToken Aborted { get; } = aborted;

    public CmsCaller Caller { get; } = caller;

    public T Service<T>() where T : notnull => services.GetRequiredService<T>();

    public T? OptionalService<T>() where T : class => services.GetService<T>();

    private IContentRepository Repository => Service<IContentRepository>();

    private bool Checked => Caller == CmsCaller.Editor;

    /// <summary>
    /// A hint that names the next step in the caller's own terms: <paramref name="developer"/> with the CLI's command
    /// (and the agent's route), <paramref name="editor"/> with the MCP tool. Neither caller is told about commands it
    /// doesn't have.
    /// </summary>
    public string ForCaller(string developer, string editor) => Checked ? editor : developer;

    /// <summary>The name the CMS records changes under: the opticli principal's for the developer, the editor's own.</summary>
    public string UserName => Checked ? Service<IPrincipalAccessor>().Principal?.Identity?.Name ?? "" : AgentProtocol.PrincipalName;

    /// <summary>
    /// Whether a publish with <c>requestApproval</c> publishes content no approval sequence applies to. For the
    /// developer it does: <c>opticli publish --request-approval</c> (and <c>apply --request-approval</c> over a plan
    /// with content of both kinds) means "put it live, through review where there is one". For an editor it never
    /// does: there <c>requestApproval</c> only ever asks for review, and so needs no publishing rights, which is what
    /// lets the MCP module leave the publish gate out for it. Where no sequence applies, the request is refused
    /// (<c>noApprovalSequence</c>).
    /// </summary>
    public bool RequestApprovalMayPublish => !Checked;

    /// <summary>
    /// Whether content in the recycle bin may be moved out of it (a restore). Not for an editor: a restore can put
    /// content that was published live again, with neither a publish nor the publish gate, so an editor restores in
    /// the CMS edit UI, where they see what comes back.
    /// </summary>
    public bool MayRestore => !Checked;

    /// <summary>
    /// Whether the caller may create content of <paramref name="type"/> below content of <paramref name="parentType"/>
    /// as far as the type's own access rights go (admin mode's access rights on a content type, <c>[Access]</c> on its
    /// class): always for the developer; for an editor as the edit UI's list of types to create offers them, which the
    /// CMS doesn't check again on save. Where the type may go at all is <c>ContentTypeAvailabilityService.IsAllowed</c>.
    /// </summary>
    public bool MayCreate(ContentType type, ContentType parentType) =>
        !Checked || Service<ContentTypeAvailabilityService>().ListAvailable(parentType.Name, Service<IPrincipalAccessor>().Principal).Any(t => t.ID == type.ID);

    /// <summary>
    /// Saves as the caller: unchecked for the developer; for an editor with the access the CMS requires for
    /// <paramref name="action"/> (Edit for a draft, Publish for a publish, Create for new content). A save that
    /// publishes or schedules passes the caller's publishing gate first, if it has one.
    /// </summary>
    public ContentReference Save(IContent content, SaveAction action)
    {
        RequirePublishing(action);
        return Checked ? Repository.Save(content, action) : Repository.Save(content, action, AccessLevel.NoAccess);
    }

    /// <summary>
    /// The caller's publishing gate, for a save with <paramref name="action"/> that publishes or schedules. <see cref="Save"/>
    /// runs it too; a caller whose save is inside a <c>catch</c> that may treat a failure as "saved after all" runs it
    /// first, outside that <c>try</c>, so the refusal can't be taken for a site failure.
    /// </summary>
    public void RequirePublishing(SaveAction action)
    {
        if (publishing is not null && (action & SaveAction.ActionMask) is SaveAction.Publish or SaveAction.Schedule)
        {
            publishing();
        }
    }

    /// <summary>
    /// Moves as the caller: for an editor, the CMS requires Read and Delete on the content, and Create below the
    /// destination (plus Publish there for published content), as the edit UI's drag and drop does.
    /// </summary>
    public void Move(ContentReference link, ContentReference destination)
    {
        if (Checked)
        {
            Repository.Move(link, destination);
        }
        else
        {
            Repository.Move(link, destination, AccessLevel.NoAccess, AccessLevel.NoAccess);
        }
    }

    /// <summary>
    /// Moves content to the recycle bin, attributed to <see cref="UserName"/>. The CMS checks Delete against the current
    /// principal either way: the developer's opticli principal has the admin roles, an editor their own.
    /// </summary>
    public void Delete(ContentReference link) => Repository.MoveToWastebasket(link, UserName);

    /// <summary>Removes a language branch: for an editor with Delete, as the edit UI's "Delete language branch" requires.</summary>
    public void DeleteLanguageBranch(ContentReference link, string language) =>
        Repository.DeleteLanguageBranch(link, language, Checked ? AccessLevel.Delete : AccessLevel.NoAccess);

    /// <summary>Whether the caller may see <paramref name="content"/>: always for the developer, with Read for an editor.</summary>
    public bool CanRead(IContent content) => !Checked || HasAccess(content, AccessLevel.Read);

    /// <summary>
    /// <see cref="IContentLoader"/> and <see cref="IContentRepository"/> load anything; an editor may only see what they
    /// can read. Content they can't read is reported exactly as content that doesn't exist, as the edit UI does, so its
    /// existence doesn't leak.
    /// </summary>
    /// <exception cref="AgentException"><c>not_found</c>, the same as <see cref="Content.ContentLocator.Resolve"/>'s for a missing id.</exception>
    public T RequireRead<T>(T content) where T : IContent =>
        CanRead(content) ? content : throw NotFound(content.ContentLink);

    /// <summary>The <c>not_found</c> for content with this id, whether it is missing or hidden from the caller.</summary>
    public static AgentException NotFound(ContentReference link) => AgentException.NotFound($"No content with id {link.ID}.");

    /// <summary>
    /// For changes the CMS doesn't check itself (access rights, which need Administer; deleting a version): an editor
    /// needs <paramref name="level"/> on <paramref name="content"/>. The developer passes.
    /// </summary>
    /// <exception cref="AgentException">
    /// <c>refused</c>; <c>not_found</c> (<see cref="RequireRead{T}"/>) for content the editor can't even read, which the
    /// refusal would name.
    /// </exception>
    public void RequireAccess(IContent content, AccessLevel level)
    {
        RequireRead(content);
        if (Checked && !HasAccess(content, level))
        {
            throw AgentException.Refused(
                $"You don't have {level} access to {content.ContentLink.ID} ('{content.Name}').",
                "Ask a CMS administrator for the access, or for someone who has it to make the change.");
        }
    }

    /// <summary>
    /// A save the CMS made, after which a site handler of its save or publish events threw (a search indexer, say): the
    /// save stands, and the caller is told so. The details go to the site's own log, where whoever runs the site looks,
    /// rather than to the caller. Without a logger (never the case in a running site) they go to standard error.
    /// </summary>
    public void SiteFailedAfterSave(ContentReference saved, Exception exception)
    {
        if (OptionalService<ILoggerFactory>() is { } loggers)
        {
            loggers.CreateLogger(LogCategory).LogWarning(exception, "The site failed after saving {Content}; the save stands.", saved.ToString());
        }
        else
        {
            Console.Error.WriteLine($"[opticli] the site failed after saving {saved}: {exception}");
        }
    }

    /// <summary>The log category of the content operations.</summary>
    public const string LogCategory = "OptiCli.Cms";

    /// <summary>Content that isn't <see cref="ISecurable"/> (a content provider's, say) has no access rights of its own, as for the CMS.</summary>
    private bool HasAccess(IContent content, AccessLevel level) =>
        content is not ISecurable securable || securable.GetSecurityDescriptor().HasAccess(Service<IPrincipalAccessor>().Principal, level);
}
