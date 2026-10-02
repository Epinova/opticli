namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Which redirect URIs a client may register and use. The code goes wherever the redirect points, so matching is exact:
/// no prefixes, no wildcards, no normalising.
/// </summary>
internal static class RedirectUris
{
    /// <summary>The longest redirect URI accepted, so a registration can't store arbitrary amounts of text.</summary>
    public const int MaxLength = 2000;

    /// <summary>
    /// https anywhere, or plain http on a loopback address (a native client's own listener, RFC 8252 7.3). Never a
    /// fragment (RFC 6749 3.1.2) or user info, which would let a URI look like it goes somewhere it doesn't.
    /// </summary>
    public static bool IsAllowed(string uri) =>
        uri.Length <= MaxLength
        && Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && u.Fragment.Length == 0 && !uri.Contains('#') && u.UserInfo.Length == 0
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && IsLoopback(u)));

    /// <summary>
    /// Exact match, except that a loopback http redirect may use any port (RFC 8252 7.3): native clients pick a free one
    /// for each sign-in. Scheme, host, path and query must still match exactly.
    /// </summary>
    public static bool Matches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal))
        {
            return IsAllowed(requested);
        }
        return Uri.TryCreate(registered, UriKind.Absolute, out var r) && Uri.TryCreate(requested, UriKind.Absolute, out var q)
            && r.Scheme == Uri.UriSchemeHttp && q.Scheme == Uri.UriSchemeHttp
            && IsLoopback(r) && IsLoopback(q)
            && string.Equals(r.Host, q.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.PathAndQuery, q.PathAndQuery, StringComparison.Ordinal)
            && IsAllowed(requested);
    }

    /// <summary>Whether any of a client's registered redirect URIs matches the requested one.</summary>
    public static bool AnyMatches(IEnumerable<string> registered, string requested) =>
        requested.Length > 0 && registered.Any(r => Matches(r, requested));

    /// <summary>127.0.0.0/8, ::1 or <c>localhost</c>.</summary>
    public static bool IsLoopback(Uri uri) => uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}
