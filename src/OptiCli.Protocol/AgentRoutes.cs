namespace OptiCli.Protocol;

/// <summary>
/// Every agent endpoint, as a path relative to <see cref="AgentProtocol.BasePath"/>.
/// </summary>
/// <remarks>
/// <para>All requests need the <see cref="AgentProtocol.TokenHeader"/> header and are only answered
/// for loopback callers while the site runs in Development. Bodies are JSON (<see cref="AgentJson"/>),
/// responses are <see cref="AgentResponse{T}"/>.</para>
/// <para><c>{ref}</c> is a content id (<c>123</c>), a version (<c>123_456</c>), a content GUID, or content from a
/// content provider (<c>63__provider</c>, as <c>ContentReference.ToString()</c> prints it). The same forms work in
/// property values, ContentArea items and link hrefs. URLs
/// and paths are resolved by the CLI before calling. Writes are saved as <see cref="AgentProtocol.PrincipalName"/>
/// and never skip versioning: drafts are always new versions, delete always goes to the recycle bin.</para>
/// <para>Errors common to every route: <c>unauthorized</c> 401, <c>refused</c> 403/503, <c>usage</c> 400
/// (bad JSON, unknown or missing request fields, bad ref), <c>not_found</c> 404 (no such route, content or type), <c>unsupported_protocol</c>
/// 404 (route for another protocol version), <c>internal</c> 500.</para>
/// </remarks>
public static class AgentRoutes
{
    /// <summary>Prefix of every route of this protocol version.</summary>
    public static readonly string Prefix = $"/v{AgentProtocol.Version}";

    /// <summary>
    /// <c>GET /v1/ping</c>. Response: <see cref="PingResponse"/>. Use it to wait for the site to start
    /// and to verify the database the CMS actually uses.
    /// </summary>
    public static readonly string Ping = $"{Prefix}/ping";

    /// <summary>
    /// <c>POST /v1/shutdown</c>, no body. Response: <see cref="ShutdownResponse"/>. Stops the site gracefully
    /// (<c>IHostApplicationLifetime.StopApplication</c>) once the response is sent: <c>serve --stop</c> uses it on every
    /// OS, before a signal or a kill.
    /// </summary>
    public static readonly string Shutdown = $"{Prefix}/shutdown";

    /// <summary>
    /// <c>GET /v1/drift</c>. Response: <see cref="DriftReport"/>: what differs between the site's code and a shared
    /// database (computed once per run, the first time it is asked for). Against a local database it isn't compared
    /// (<see cref="DriftReport.Checked"/> false). While there are differences, writes that aren't dry runs fail with
    /// <c>drift</c> 409 unless they send <see cref="AgentProtocol.AcceptDriftHeader"/> with the fingerprint.
    /// </summary>
    public static readonly string Drift = $"{Prefix}/drift";

    /// <summary>
    /// <c>GET /v1/types/{name}</c>, name or GUID. Response: <see cref="ContentTypeModel"/>.
    /// Errors: <c>not_found</c> with close matches in the hint.
    /// </summary>
    public static string Type(string nameOrGuid) => $"{Prefix}/types/{Uri.EscapeDataString(nameOrGuid)}";

    /// <summary>
    /// <c>GET /v1/types-without-code</c>. Response: <see cref="TypesWithoutCodeResult"/>: the content types whose model
    /// type the running site can't load (<c>ContentType.ModelType</c> null while <c>ModelTypeString</c> is set), for
    /// <c>opticli types --orphaned</c>. Read-only.
    /// </summary>
    public static readonly string TypesWithoutCode = $"{Prefix}/types-without-code";

    /// <summary>
    /// <c>POST /v1/types/remove</c>. Body: <see cref="OrphanRemovalRequest"/>. Response: <see cref="OrphanRemovalResult"/>.
    /// Removes content types whose class the running site can't load and properties that aren't in their type's code,
    /// through <c>IContentTypeRepository.Delete</c> and <c>IPropertyDefinitionRepository.Delete</c>; nothing else. Errors
    /// for named items (all are checked before anything is removed): <c>refused</c> (not an orphan: made in admin mode, a
    /// system type, a class the site loads, a property in the code; stored values without
    /// <see cref="OrphanRemovalRequest.AllowDestructive"/>; anything in shared mode), <c>conflict</c> (content of the
    /// type, also in the recycle bin, or a property that uses it as its block type), <c>not_found</c>; each item is in
    /// <c>validation</c>. Developer-only: the MCP module has no such tool.
    /// </summary>
    public static readonly string TypesRemove = $"{Prefix}/types/remove";

    /// <summary>
    /// <c>POST /v1/sites/hosts</c>. Body: <see cref="SiteHostsRequest"/>. Response: <see cref="SiteHostsResult"/>. Saves
    /// through <c>ISiteDefinitionRepository</c>, which clears the site definition cache and raises its change events, so
    /// the running site uses the new hosts at once. Listing sites stays a database read (<c>opticli sites</c>).
    /// Errors: <c>not_found</c> (site, with close matches; a host to remove), <c>conflict</c> (adding a host the site has),
    /// <c>validation</c> 422 naming the offending change (a host another site has, two primary hosts for a language, a
    /// language that isn't enabled, a bad host name, ...), <c>refused</c> (the site's last host; in shared mode, anything
    /// but adding a host of type undefined: <see cref="SiteHostsRequest.AllowedOnSharedDatabase"/>).
    /// Developer-only: the MCP module has no such tool.
    /// </summary>
    public static readonly string SiteHosts = $"{Prefix}/sites/hosts";

    /// <summary>
    /// <c>POST /v1/jobs/run</c>. Body: <see cref="JobRunRequest"/>. Response: <see cref="JobRunResult"/>. Starts the job
    /// through <c>IScheduledJobExecutor</c> with a user trigger, as the admin UI's "Start manually" does, also while the
    /// scheduler is off, and returns once it has started. The job runs as <see cref="AgentProtocol.PrincipalName"/>.
    /// Errors: <c>not_found</c> (job), <c>conflict</c> (it is running already), <c>refused</c> (a
    /// <see cref="DestructiveJobs"/> job without <see cref="JobRunRequest.AllowDestructive"/>; anything in shared mode).
    /// Developer-only: the MCP module has no such tool.
    /// </summary>
    public static readonly string JobRun = $"{Prefix}/jobs/run";

    /// <summary>
    /// <c>POST /v1/jobs/stop</c>. Body: <see cref="JobStopRequest"/>. Response: <see cref="JobStopResult"/>. Asks a job
    /// this site runs to stop (<c>IScheduledJobExecutor.Cancel</c>, which calls the job's <c>Stop()</c>).
    /// Errors: <c>not_found</c>, <c>conflict</c> (not running here), <c>refused</c> (the job can't be stopped; shared mode).
    /// </summary>
    public static readonly string JobStop = $"{Prefix}/jobs/stop";

    /// <summary>
    /// <c>POST /v1/jobs/set</c>. Body: <see cref="JobSetRequest"/>. Response: <see cref="JobSetResult"/>. Saves through
    /// <c>IScheduledJobRepository.Save</c>, as the admin UI does. Errors: <c>not_found</c>, <c>usage</c> (an interval or
    /// time it can't take), <c>refused</c> (shared mode).
    /// </summary>
    public static readonly string JobSet = $"{Prefix}/jobs/set";

    /// <summary>
    /// <c>POST /v1/users/add</c>. Body: <see cref="UserAddRequest"/>. Response: <see cref="UserAddResult"/>. Creates a user
    /// through the site's ASP.NET Identity (<c>UserManager</c> for the user class the CMS UI's user provider uses), tagged
    /// with <see cref="LocalUsers.CreatedClaim"/>, in the given roles (created if missing). Errors: <c>conflict</c> (the
    /// name exists), <c>validation</c> (the site's password or user name rules), <c>refused</c> (a site without ASP.NET
    /// Identity; shared mode). Developer-only: the MCP module has no such tool.
    /// </summary>
    public static readonly string UserAdd = $"{Prefix}/users/add";

    /// <summary>
    /// <c>POST /v1/users/remove</c>. Body: <see cref="UserRemoveRequest"/>. Response: <see cref="UserRemoveResult"/>.
    /// Deletes a user opticli made. Errors: <c>not_found</c>, <c>refused</c> (a user opticli didn't make; no ASP.NET
    /// Identity; shared mode).
    /// </summary>
    public static readonly string UserRemove = $"{Prefix}/users/remove";

    /// <summary>
    /// <c>GET /v1/users/roles</c>. Response: <see cref="UserRolesResult"/>: role names with member counts and the virtual
    /// role mapping; never user names or addresses. Errors: <c>refused</c> (no ASP.NET Identity; shared mode).
    /// </summary>
    public static readonly string UserRoles = $"{Prefix}/users/roles";

    /// <summary>
    /// <c>POST /v1/content</c>. Body: <see cref="CreateRequest"/>. Response: <see cref="WriteResult"/>
    /// (201 when saved). Errors: <c>not_found</c> (parent, type), <c>usage</c> (unknown property, bad value),
    /// <c>validation</c> 422 (required property, type not allowed under the parent, ...), <c>conflict</c> 409 (the GUID
    /// exists, or <see cref="CreateRequest.UpdateExisting"/> would publish someone else's unpublished changes).
    /// </summary>
    public static readonly string Create = $"{Prefix}/content";

    /// <summary>
    /// <c>POST /v1/media</c>. Body: <see cref="UploadRequest"/> (up to <see cref="UploadRequest.MaxBytes"/> of file content).
    /// Response: <see cref="WriteResult"/> (201 when saved), as for <see cref="Create"/>. Errors: <c>usage</c> when no
    /// media type accepts the extension or <c>type</c> doesn't, <c>not_found</c> (parent, type), <c>validation</c> 422.
    /// </summary>
    public static readonly string Media = $"{Prefix}/media";

    /// <summary>
    /// <c>POST /v1/content/{ref}/draft</c>. Body: <see cref="DraftRequest"/>. Response: <see cref="WriteResult"/>.
    /// Always saves a new version (<c>Save | ForceNewVersion</c>, or <c>Publish | ForceNewVersion</c>),
    /// based on the ref's version if it has one, else the latest version in the language. A saved draft
    /// becomes the primary (common) draft, the version edit mode opens.
    /// Errors: <c>conflict</c> 409 when <see cref="DraftRequest.BaseVersion"/> is not the latest
    /// (<see cref="AgentError.CurrentVersion"/> says which is), or when a publish would include someone else's
    /// unpublished changes without <see cref="DraftRequest.IncludeDraft"/> (<see cref="AgentError.PendingDraft"/>);
    /// <c>validation</c> 422, <c>usage</c> 400.
    /// </summary>
    public static string Draft(string contentRef) => $"{Content(contentRef)}/draft";

    /// <summary>
    /// <c>POST /v1/content/{ref}/languages</c>. Body: <see cref="LanguageBranchRequest"/>. Response:
    /// <see cref="WriteResult"/> (201). Errors: <c>conflict</c> when the branch already exists,
    /// <c>usage</c> when the language is not enabled, <c>validation</c> 422.
    /// </summary>
    public static string Languages(string contentRef) => $"{Content(contentRef)}/languages";

    /// <summary>
    /// <c>POST /v1/content/{ref}/remove-language</c>. Body: <see cref="RemoveLanguageRequest"/>. Deletes a language branch
    /// with all its versions (<c>IContentRepository.DeleteLanguageBranch</c>); it can't be undone. Response:
    /// <see cref="RemoveLanguageResult"/>. Errors: <c>refused</c> for the master language and site start pages;
    /// <c>not_found</c> when there is no such branch.
    /// </summary>
    public static string RemoveLanguage(string contentRef) => $"{Content(contentRef)}/remove-language";

    /// <summary>
    /// <c>POST /v1/content/{ref}/publish</c>. Body: <see cref="PublishRequest"/> (optional). Publishes the
    /// given version, the ref's version, or the latest version in the language. Response: <see cref="WriteResult"/>.
    /// Errors: <c>conflict</c> when that version is already published, or when the latest version includes someone
    /// else's unpublished changes and neither a version nor <see cref="PublishRequest.IncludeDraft"/> was given
    /// (<see cref="AgentError.PendingDraft"/>); <c>validation</c> 422.
    /// </summary>
    public static string Publish(string contentRef) => $"{Content(contentRef)}/publish";

    /// <summary>
    /// <c>POST /v1/content/{ref}/unpublish</c>. Body: <see cref="UnpublishRequest"/> (optional). Takes the branch offline as
    /// the edit UI does: a copy of the published version with its stop-publish date set to now is published, and the
    /// common draft stays the one edit mode opens. Response: <see cref="WriteResult"/> with
    /// <see cref="WriteResult.Unpublished"/>. Errors: <c>refused</c> for start pages, site and asset roots, and content
    /// with an approval sequence; <c>conflict</c> when the branch isn't published or already expired.
    /// </summary>
    public static string Unpublish(string contentRef) => $"{Content(contentRef)}/unpublish";

    /// <summary>
    /// <c>POST /v1/content/{ref}/discard</c>. Body: <see cref="DiscardRequest"/> (optional). Deletes one unpublished version
    /// (<c>IContentVersionRepository.Delete</c>); it can't be undone. Response: <see cref="WriteResult"/> with
    /// <see cref="WriteResult.Discarded"/>. Errors: <c>refused</c> for the published version and the only version;
    /// <c>conflict</c> for a version in review, or one someone else saved without <see cref="DiscardRequest.IncludeDraft"/>
    /// (<see cref="AgentError.PendingDraft"/>).
    /// </summary>
    public static string Discard(string contentRef) => $"{Content(contentRef)}/discard";

    /// <summary>
    /// <c>POST /v1/content/{ref}/move</c>. Body: <see cref="MoveRequest"/>. Response: <see cref="MoveResult"/>.
    /// Errors: <c>refused</c> for protected content (root, recycle bin, start pages) or a move into the
    /// recycle bin (use delete), <c>usage</c> for a move below itself, <c>validation</c> when the content's type isn't
    /// allowed below the new parent.
    /// </summary>
    public static string Move(string contentRef) => $"{Content(contentRef)}/move";

    /// <summary>
    /// <c>GET /v1/restore-parents?ids=1,2,3</c> (at most <see cref="RestoreParentsResult.MaxIds"/> ids). Response:
    /// <see cref="RestoreParentsResult"/>: for each item that has one, the parent the CMS stored when it was last moved
    /// (<c>IParentRestoreRepository</c>, what the edit UI's Restore and <see cref="Restore"/> use). Read-only; for
    /// <c>opticli trash</c>. Developer-only: the MCP module has no such tool.
    /// </summary>
    public static string RestoreParents(IEnumerable<int> ids) =>
        $"{Prefix}/restore-parents?ids={string.Join(",", ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)))}";

    /// <summary>
    /// <c>POST /v1/content/{ref}/restore</c>. Body: <see cref="RestoreRequest"/>. Response: <see cref="RestoreResult"/>.
    /// Moves content that was deleted out of the recycle bin, as the edit UI's Restore does: below the parent the CMS
    /// stored when it was deleted (<c>IParentRestoreRepository</c>), or below <see cref="RestoreRequest.Parent"/>.
    /// Errors: <c>conflict</c> when it isn't in the recycle bin, or the parent is (or is gone); <c>usage</c> for content
    /// below deleted content (restore what was deleted), and when no parent was stored and none given; <c>validation</c>
    /// when its type isn't allowed below the parent; <c>refused</c> for an editor. The MCP module has no such tool.
    /// </summary>
    public static string Restore(string contentRef) => $"{Content(contentRef)}/restore";

    /// <summary>
    /// <c>POST /v1/content/{ref}/access</c>. Body: <see cref="AccessRequest"/>. Response: <see cref="AccessResult"/>.
    /// Replaces the item's ACL (<c>SecuritySaveType.Replace</c>); descendants that inherit follow, nothing else is changed.
    /// Access rights aren't versioned: <see cref="AccessResult.Before"/> is the only record of the previous state.
    /// Errors: <c>refused</c> for protected content (root, recycle bin, start pages, asset roots, the global block
    /// folder) and for a change that leaves no admin role with Administer; <c>usage</c> for an inherited ACL changed
    /// without <c>breakInheritance</c>, unknown roles (unless <c>allowUnknownRole</c>) and bad levels.
    /// </summary>
    public static string Access(string contentRef) => $"{Content(contentRef)}/access";

    /// <summary>
    /// <c>DELETE /v1/content/{ref}</c>. Moves the content (and its descendants) to the recycle bin; nothing
    /// is ever deleted permanently. Response: <see cref="MoveResult"/>. Errors: <c>refused</c> for
    /// protected content, <c>conflict</c> when it is already in the recycle bin.
    /// </summary>
    public static string Delete(string contentRef) => Content(contentRef);

    /// <summary>
    /// <c>GET /v1/content/{ref}?lang=&amp;version=</c>. Read-only: loads the item through <c>IContentLoader</c>
    /// (and <c>IContentVersionRepository</c> for versions) and returns it as <see cref="ContentItem"/>.
    /// <c>version</c> is <c>published</c> (default: the primary version, which is the latest draft when never
    /// published), <c>latest</c>, or a version id; a <c>123_456</c> ref also selects a version. <c>lang</c>
    /// defaults to the master language and is ignored for content that isn't localizable; a version is always
    /// shown in its own language. Errors: <c>not_found</c> (content, version, or no branch in <c>lang</c>),
    /// <c>usage</c> (unknown query parameter, bad version, both a versioned ref and <c>version</c>).
    /// </summary>
    public static string Read(string contentRef, string? lang = null, string? version = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(lang))
        {
            query.Add($"{ReadQuery.Language}={Uri.EscapeDataString(lang)}");
        }
        if (!string.IsNullOrEmpty(version))
        {
            query.Add($"{ReadQuery.Version}={Uri.EscapeDataString(version)}");
        }
        return query.Count == 0 ? Content(contentRef) : $"{Content(contentRef)}?{string.Join('&', query)}";
    }

    private static string Content(string contentRef) => $"{Create}/{Uri.EscapeDataString(contentRef)}";
}

/// <summary>Query parameters of <see cref="AgentRoutes.Read"/>.</summary>
public static class ReadQuery
{
    public const string Language = "lang";

    public const string Version = "version";

    public const string Published = "published";

    public const string Latest = "latest";

    public static readonly IReadOnlyList<string> All = [Language, Version];
}
