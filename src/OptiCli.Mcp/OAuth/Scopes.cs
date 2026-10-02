namespace OptiCli.Mcp.OAuth;

/// <summary>
/// What an editor can allow a client. Scopes only ever narrow what the editor may do: the CMS's access rights and the
/// site's options still decide every call.
/// </summary>
public static class Scopes
{
    /// <summary>Read content the editor has Read access to.</summary>
    public const string Read = "content:read";

    /// <summary>Create content and change drafts.</summary>
    public const string Write = "content:write";

    /// <summary>Publish, unpublish and schedule publishing; only offered when the site sets <see cref="OptiCliMcpOptions.AllowPublish"/>.</summary>
    public const string Publish = "content:publish";

    /// <returns>The scopes this site offers, in a fixed order.</returns>
    internal static string[] Supported(OptiCliMcpOptions options) => options.AllowPublish ? [Read, Write, Publish] : [Read, Write];

    /// <summary>
    /// What a client may get for the scopes it asked for: the ones the site offers, in their fixed order; all of them
    /// when it asked for none. Unknown scopes and <c>content:publish</c> on a site without publishing are left out
    /// rather than refused, so a client that always asks for everything still connects. <see cref="Read"/> comes with
    /// anything else, as the MCP endpoint needs it for every call.
    /// </summary>
    /// <returns>Space-separated scopes; empty when nothing asked for is offered.</returns>
    internal static string Grantable(string? requested, OptiCliMcpOptions options)
    {
        var supported = Supported(options);
        var asked = Split(requested);
        if (asked.Count == 0)
        {
            return string.Join(' ', supported);
        }
        var offered = supported.Where(asked.Contains).ToList();
        return offered.Count == 0 ? "" : string.Join(' ', supported.Where(s => s == Read || offered.Contains(s)));
    }

    /// <summary>
    /// What the editor allowed on the consent page: <see cref="Read"/>, which is required, and those of the
    /// <paramref name="offered"/> scopes they left ticked. Anything posted that wasn't offered is ignored.
    /// </summary>
    /// <param name="offered">Space-separated, as <see cref="Grantable"/> gives them.</param>
    internal static string Chosen(string offered, IEnumerable<string?> ticked)
    {
        var allowed = new HashSet<string>(ticked.OfType<string>(), StringComparer.Ordinal) { Read };
        return string.Join(' ', offered.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(allowed.Contains));
    }

    /// <summary>
    /// The scopes of <paramref name="granted"/> that <paramref name="requested"/> names, in <paramref name="granted"/>'s
    /// order: a refresh asking for less (RFC 6749 6) gets an access token for that much.
    /// </summary>
    /// <returns>Space-separated; <paramref name="granted"/> when nothing was requested.</returns>
    internal static string Narrowed(string granted, string? requested) => Split(requested).Count == 0 ? granted : Intersect(granted, requested);

    /// <summary>The scopes of <paramref name="granted"/> that <paramref name="other"/> has too, in <paramref name="granted"/>'s order.</summary>
    internal static string Intersect(string granted, string? other)
    {
        var also = Split(other);
        return string.Join(' ', granted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(also.Contains));
    }

    internal static HashSet<string> Split(string? scope) =>
        new((scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

    /// <summary>How the consent page describes a scope.</summary>
    internal static string Describe(string scope) => scope switch
    {
        Read => "Read the content you have access to",
        Write => "Create content and change drafts (saved as drafts, not published)",
        Publish => "Publish and unpublish content",
        _ => scope,
    };
}
