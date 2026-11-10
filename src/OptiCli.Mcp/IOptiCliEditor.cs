using EPiServer.Core;

namespace OptiCli.Mcp;

/// <summary>
/// The editor a site's own MCP tool runs for (<see cref="OptiCliMcpBuilderExtensions.WithTools{T}"/>): who they are,
/// what they approved for this connection, and the content they may read. Take it in the tool class's constructor (or
/// as a parameter of the tool method), as any service.
/// </summary>
/// <remarks>
/// <para>
/// The CMS's <c>IContentLoader</c> and <c>IContentRepository</c> load anything, whatever the editor's access rights:
/// load the content a tool returns through this (<see cref="Load{T}"/>), or filter what a search index or the site's own
/// code found with <see cref="Readable(IEnumerable{ContentReference}, string?)"/>. Content the editor can't read must
/// not exist for the assistant, as it doesn't in the module's own tools: not in a result, not in a count, not in an
/// error message.
/// </para>
/// <para>
/// Only there on the module's MCP endpoint, for a signed-in editor: resolving it anywhere else (another MCP server's
/// tool, a page, a scheduled job) throws.
/// </para>
/// </remarks>
public interface IOptiCliEditor
{
    /// <summary>The editor's user name, as the CMS knows them.</summary>
    string Name { get; }

    /// <summary>The roles the editor had when the connection last got a token (the CMS works out virtual roles on each check).</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>The scopes the editor approved for this connection, e.g. <c>content:read</c>, <c>content:write</c> (<see cref="OAuth.Scopes"/>).</summary>
    IReadOnlyList<string> Scopes { get; }

    /// <summary>Whether the editor approved <paramref name="scope"/> for this connection (<see cref="OAuth.Scopes"/>).</summary>
    bool HasScope(string scope);

    /// <summary>
    /// Loads content as <c>get_content</c> does, for the editor: in <paramref name="lang"/> (the master language when
    /// left out), the published version, or with <paramref name="latest"/> the newest one (often a draft).
    /// </summary>
    /// <param name="link">The content; a version (<c>123_456</c>) loads that version.</param>
    /// <param name="lang">A language branch the content has, e.g. <c>en</c>.</param>
    /// <param name="latest">The newest version rather than the published one: what a change should be planned from.</param>
    /// <exception cref="OptiCliToolException">
    /// <c>not_found</c> for content that doesn't exist and for content the editor can't read, the same answer for both;
    /// <c>usage</c> when it isn't a <typeparamref name="T"/>. Let it propagate: the assistant gets it as the tool's error.
    /// </exception>
    /// <exception cref="ArgumentException">A version together with <paramref name="latest"/>.</exception>
    T Load<T>(ContentReference link, string? lang = null, bool latest = false) where T : IContent;

    /// <summary>Whether the editor may read <paramref name="content"/> (Read access to it).</summary>
    bool CanRead(IContent content);

    /// <summary>
    /// Loads <paramref name="links"/> and keeps what the editor may read, in the same order: what a search index or the
    /// site's own code found, made safe to return. Content that no longer exists is left out too.
    /// </summary>
    /// <param name="lang">
    /// The language to load each item in, falling back to its master language where it has no such branch; each item's
    /// master language when left out.
    /// </param>
    /// <exception cref="OptiCliToolException"><c>usage</c> when <paramref name="lang"/> isn't a language code.</exception>
    IReadOnlyList<IContent> Readable(IEnumerable<ContentReference> links, string? lang = null);

    /// <summary>Keeps the content the editor may read, in the same order.</summary>
    IReadOnlyList<T> Readable<T>(IEnumerable<T> contents) where T : IContent;

    /// <summary>
    /// <paramref name="content"/> as the module's own tools describe content, with its public URL and the edit UI's link
    /// to the version: what a tool returns for each item it found.
    /// </summary>
    /// <exception cref="OptiCliToolException">
    /// <c>not_found</c> for content the editor can't read, as if it didn't exist: filter with <see cref="Readable{T}"/>
    /// first.
    /// </exception>
    ContentLinkSummary Describe(IContent content);
}
