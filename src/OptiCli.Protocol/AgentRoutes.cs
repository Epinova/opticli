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
    /// <c>GET /v1/types/{name}</c>, name or GUID. Response: <see cref="ContentTypeModel"/>.
    /// Errors: <c>not_found</c> with close matches in the hint.
    /// </summary>
    public static string Type(string nameOrGuid) => $"{Prefix}/types/{Uri.EscapeDataString(nameOrGuid)}";

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
    /// <c>POST /v1/content/{ref}/publish</c>. Body: <see cref="PublishRequest"/> (optional). Publishes the
    /// given version, the ref's version, or the latest version in the language. Response: <see cref="WriteResult"/>.
    /// Errors: <c>conflict</c> when that version is already published, or when the latest version includes someone
    /// else's unpublished changes and neither a version nor <see cref="PublishRequest.IncludeDraft"/> was given
    /// (<see cref="AgentError.PendingDraft"/>); <c>validation</c> 422.
    /// </summary>
    public static string Publish(string contentRef) => $"{Content(contentRef)}/publish";

    /// <summary>
    /// <c>POST /v1/content/{ref}/move</c>. Body: <see cref="MoveRequest"/>. Response: <see cref="MoveResult"/>.
    /// Errors: <c>refused</c> for protected content (root, recycle bin, start pages) or a move into the
    /// recycle bin (use delete), <c>usage</c> for a move below itself.
    /// </summary>
    public static string Move(string contentRef) => $"{Content(contentRef)}/move";

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
