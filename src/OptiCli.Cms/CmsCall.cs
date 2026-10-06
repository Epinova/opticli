using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OptiCli.Cms.Content;
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
/// <param name="deleting">
/// Runs before a change that deletes for good what someone else made (<see cref="RequireDeleting"/>), and throws to stop
/// it: the MCP module's gate for <c>AllowDelete</c>. Null lets it through, as for the developer.
/// </param>
internal sealed class CmsCall(IServiceProvider services, CancellationToken aborted, CmsCaller caller, Action? publishing = null, Action? deleting = null)
{
    /// <summary>Signalled when the caller gave up (a timeout or Ctrl+C in the CLI): nothing is saved after that.</summary>
    public CancellationToken Aborted { get; } = aborted;

    public CmsCaller Caller { get; } = caller;

    public T Service<T>() where T : notnull => services.GetRequiredService<T>();

    public T? OptionalService<T>() where T : class => services.GetService<T>();

    private IContentRepository Repository => Service<IContentRepository>();

    private bool Checked => Caller == CmsCaller.Editor;

    private EditUiProperties? _properties;

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
    /// Whether the caller may create content of <paramref name="type"/> below <paramref name="parent"/>, of
    /// <paramref name="parentType"/>, as far as access rights go: always for the developer; for an editor as the edit
    /// UI's list of types to create offers them, which the CMS doesn't check again on save. That list leaves out a type
    /// whose own access rights (admin mode's, <c>[Access]</c> on its class) don't let the editor create it, and one in a
    /// group of types (<c>[GroupDefinitions]</c>, admin mode's content type groups) whose required access the editor
    /// lacks on the parent. Where the type may go at all is <c>ContentTypeAvailabilityService.IsAllowed</c>.
    /// </summary>
    /// <param name="parent">
    /// The parent itself, when it is of <paramref name="parentType"/>; null (or a parent of another type: a dry run's
    /// stand-in, or the owner of a "For this page" folder that doesn't exist yet) checks the type's own access rights only.
    /// </param>
    public bool MayCreate(ContentType type, ContentType parentType, IContent? parent = null)
    {
        if (!Checked)
        {
            return true;
        }
        var availability = Service<ContentTypeAvailabilityService>();
        var principal = Service<IPrincipalAccessor>().Principal;
        var offered = parent is not null && parent.ContentTypeID == parentType.ID
            ? availability.ListAvailable(parent, contentFolder: false, principal)
            : availability.ListAvailable(parentType.Name, principal);
        return offered.Any(t => t.ID == type.ID);
    }

    /// <summary>
    /// Whether rich text (XhtmlString) may hold script: <c>&lt;script&gt;</c>, event handler attributes,
    /// <c>javascript:</c> URLs and the like (<see cref="MarkupSafety"/>), and links may have any scheme. For the developer
    /// they may, as in the content they import. For an editor they may not: the CMS saves rich text as it is given (its
    /// script parser keeps script by default, where it has one, and the edit UI's TinyMCE only cleans in the browser), and
    /// a draft with script runs it in the edit UI of whoever opens it, with their session. Content the assistant read could
    /// tell it to write just that.
    /// </summary>
    public bool MayWriteScript => !Checked;

    /// <summary>
    /// Whether content references may be given in the CMS's own text form, which isn't looked up: a ContentArea or a
    /// reference list as stored, a content reference as a bare number, an embedded block's id or GUID in rich text. For
    /// the developer, who reads everything, they may. For an editor every reference goes through the read check
    /// (<see cref="Content.ContentLocator"/>), as the edit UI's pickers only offer what they can read: otherwise a dry
    /// run's validation would name the type of content they can't read, and a save could point to it.
    /// </summary>
    public bool MayReferenceUnchecked => !Checked;

    /// <summary>
    /// Whether a move of content that may be live is left to the CMS's own checks. For the developer it is. For an editor,
    /// it passes the publishing gate, and isn't made from one approval sequence to another (<c>MoveOperation</c>): a move
    /// changes live URLs at once, the moved content inherits the access rights of its new parent (a page moved out of a
    /// members-only section becomes public), and content moved below another sequence skips its review. The CMS itself
    /// only asks for Publish on the destination when the moved item itself is published.
    /// </summary>
    public bool ChecksLiveMoves => Checked;

    /// <summary>
    /// Saves as the caller: unchecked for the developer; for an editor with the access the CMS requires for
    /// <paramref name="action"/> (Edit for a draft, Publish for a publish, Create for new content), and edit access to
    /// the content's language (<see cref="RequireLanguageAccess(IContent)"/>). A save that publishes or schedules, or of content
    /// without versions, passes the caller's publishing gate first, if it has one (<see cref="RequirePublishing"/>).
    /// </summary>
    public ContentReference Save(IContent content, SaveAction action)
    {
        RequireLanguageAccess(content);
        RequirePublishing(action, content);
        return Checked ? Repository.Save(content, action) : Repository.Save(content, action, AccessLevel.NoAccess);
    }

    /// <summary>
    /// The caller's publishing gate, for a save with <paramref name="action"/> that publishes or schedules, or of
    /// <paramref name="content"/> that has no versions (isn't <see cref="IVersionable"/>): a save of that is live at once,
    /// whatever the action. Folders are the exception: they have no versions, and nothing of theirs is shown to visitors.
    /// <see cref="Save"/> runs it too; a caller whose save is inside a <c>catch</c> that may treat a failure as "saved
    /// after all" runs it first, outside that <c>try</c>, so the refusal can't be taken for a site failure.
    /// </summary>
    public void RequirePublishing(SaveAction action, IContent? content = null)
    {
        if (publishing is not null
            && ((action & SaveAction.ActionMask) is SaveAction.Publish or SaveAction.Schedule || content is not (null or IVersionable or ContentFolder)))
        {
            publishing();
        }
    }

    /// <summary>
    /// The caller's deleting gate, for a change that deletes for good what someone else made: discarding a colleague's
    /// draft. <c>includeDraft</c> only confirms it, and the assistant sets that itself, so for an editor it takes what
    /// deleting takes (<c>AllowDelete</c>). Nothing happens without a gate, as for the developer.
    /// </summary>
    public void RequireDeleting() => deleting?.Invoke();

    /// <summary>
    /// For an editor, edit access to the language of <paramref name="content"/>, as admin mode sets it per language (the
    /// edit UI checks it; the CMS's repository doesn't). Content that isn't localizable has no language, and the developer
    /// passes.
    /// </summary>
    /// <exception cref="AgentException"><c>refused</c>.</exception>
    public void RequireLanguageAccess(IContent content)
    {
        if (content is ILocalizable { Language: { } language } && !CultureInfo.InvariantCulture.Equals(language))
        {
            RequireLanguageAccess(language);
        }
    }

    /// <inheritdoc cref="RequireLanguageAccess(IContent)"/>
    public void RequireLanguageAccess(CultureInfo language)
    {
        if (!Checked)
        {
            return;
        }
        var branch = Service<ILanguageBranchRepository>().Load(language);
        if (branch is null || !branch.QueryEditAccessRights(Service<IPrincipalAccessor>().Principal))
        {
            throw AgentException.Refused(
                $"You don't have access to edit content in '{language.Name}': the site limits who may edit that language.",
                "Ask a CMS administrator for access to the language (admin mode, Languages), or for someone who has it to make the change. Nothing was changed.");
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
    /// Moves content to the recycle bin, attributed to <see cref="UserName"/>. On CMS 12 the CMS checks Delete against the
    /// current principal either way: the developer's opticli principal has the admin roles, an editor their own. On CMS 13
    /// it checks the access level given: Delete for an editor, none for the developer, as for a save.
    /// </summary>
    public void Delete(ContentReference link) =>
        Compat.CmsApi.MoveToWastebasket(Repository, link, UserName, Checked ? AccessLevel.Delete : AccessLevel.NoAccess);

    /// <summary>
    /// Removes a language branch: for an editor with Delete, as the edit UI's "Delete language branch" requires, and edit
    /// access to the language (<see cref="RequireLanguageAccess(IContent)"/>).
    /// </summary>
    public void DeleteLanguageBranch(ContentReference link, string language)
    {
        if (Checked)
        {
            RequireLanguageAccess(CultureInfo.GetCultureInfo(language));
        }
        Repository.DeleteLanguageBranch(link, language, Checked ? AccessLevel.Delete : AccessLevel.NoAccess);
    }

    /// <summary>
    /// Deletes one version for good. CMS 12 checks nothing here (CMS 13 checks Delete for an editor, nothing for the
    /// developer); the caller has checked Delete on the content (<see cref="RequireAccess"/>). For an editor, this also
    /// takes edit access to the version's language.
    /// </summary>
    public void DeleteVersion(IContent version)
    {
        RequireLanguageAccess(version);
        Compat.CmsApi.DeleteVersion(Service<IContentVersionRepository>(), version.ContentLink, Checked ? AccessLevel.Delete : AccessLevel.NoAccess);
    }

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

    /// <summary>
    /// Which properties the caller may see and change: every one for the developer; for an editor, those the CMS edit UI
    /// shows them, and of those only the ones it lets them change (<see cref="EditUiProperties"/>).
    /// </summary>
    public EditUiProperties Properties => _properties ??= new EditUiProperties(this, Checked);

    /// <summary>Content that isn't <see cref="ISecurable"/> (a content provider's, say) has no access rights of its own, as for the CMS.</summary>
    private bool HasAccess(IContent content, AccessLevel level) =>
        content is not ISecurable securable || securable.GetSecurityDescriptor().HasAccess(Service<IPrincipalAccessor>().Principal, level);
}
