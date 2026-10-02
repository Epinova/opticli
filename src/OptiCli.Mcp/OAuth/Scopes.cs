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
    /// What a client gets for the scopes it asked for: the ones the site offers, in their fixed order; all of them
    /// when it asked for none. Unknown scopes and <c>content:publish</c> on a site without publishing are left out
    /// rather than refused, so a client that always asks for everything still connects.
    /// </summary>
    /// <returns>Space-separated scopes; empty when nothing asked for is offered.</returns>
    internal static string Grantable(string? requested, OptiCliMcpOptions options)
    {
        var supported = Supported(options);
        var asked = Split(requested);
        return asked.Count == 0 ? string.Join(' ', supported) : string.Join(' ', supported.Where(asked.Contains));
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
