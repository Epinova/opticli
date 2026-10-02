using System.Text.Json;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Client ID Metadata Documents (draft-ietf-oauth-client-id-metadata-document): the <c>client_id</c> is an HTTPS URL,
/// and the JSON document there describes the client. Claude prefers them: nothing is stored per connection, and the
/// client is who its domain says it is.
/// </summary>
internal static class ClientMetadataDocument
{
    /// <summary>The largest document accepted, so a client_id can't make the site download much.</summary>
    public const int MaxBytes = 5 * 1024;

    public const int MaxRedirectUris = 20;

    public const int MaxNameLength = 100;

    public static bool IsUrl(string clientId) =>
        clientId.StartsWith("https://", StringComparison.Ordinal) || clientId.StartsWith("http://", StringComparison.Ordinal);

    /// <summary>
    /// Checks the URL as the draft requires: https, with a path, without a fragment, user info or dot segments.
    /// Plain http only when <paramref name="allowHttpLoopback"/> (Development) and the host is a loopback one.
    /// </summary>
    /// <returns>Why the URL is refused; null when it may be fetched.</returns>
    public static string? CheckUrl(string clientId, bool allowHttpLoopback, out Uri? url)
    {
        url = null;
        if (clientId.Length > RedirectUris.MaxLength || !Uri.TryCreate(clientId, UriKind.Absolute, out var parsed))
        {
            return "isn't a URL";
        }
        if (parsed.Scheme != Uri.UriSchemeHttps && !(allowHttpLoopback && parsed.Scheme == Uri.UriSchemeHttp && RedirectUris.IsLoopback(parsed)))
        {
            return "isn't https";
        }
        if (clientId.Contains('#') || parsed.UserInfo.Length > 0)
        {
            return "has a fragment or user info";
        }
        var path = clientId[(clientId.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = path.IndexOf('/');
        var segments = slash < 0 ? [] : path[slash..].Split('?')[0].Split('/');
        if (slash < 0 || parsed.AbsolutePath == "/" || segments.Any(s => s is "." or ".." || s.Equals("%2e", StringComparison.OrdinalIgnoreCase)
            || s.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase)))
        {
            return "has no path, or a dot segment in it";
        }
        url = parsed;
        return null;
    }

    /// <summary>
    /// Reads a fetched document. It must name itself (its <c>client_id</c> is exactly the URL it came from), list
    /// allowed redirect URIs, and be a public client: there is no secret a document could share safely.
    /// </summary>
    /// <returns>The client, or why the document is refused.</returns>
    public static (RegisteredClient? Client, string? Error) Parse(string clientId, ReadOnlySpan<byte> json, DateTimeOffset now)
    {
        if (json.Length > MaxBytes)
        {
            return (null, $"is larger than {MaxBytes} bytes");
        }
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 8 });
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return (null, "isn't JSON");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "isn't a JSON object");
            }
            if (!root.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != clientId)
            {
                return (null, "has a client_id other than its own URL");
            }
            if (root.TryGetProperty("token_endpoint_auth_method", out var method)
                && (method.ValueKind != JsonValueKind.String || method.GetString() != ClientAuthMethods.None))
            {
                return (null, "isn't a public client (token_endpoint_auth_method must be none)");
            }
            if (root.TryGetProperty("client_secret", out _) || root.TryGetProperty("client_secret_expires_at", out _))
            {
                return (null, "contains a client secret");
            }
            if (!Contains(root, "grant_types", "authorization_code") || !Contains(root, "response_types", "code"))
            {
                return (null, "doesn't use the authorization code flow");
            }
            if (!root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array
                || uris.GetArrayLength() is 0 or > MaxRedirectUris
                || uris.EnumerateArray().Any(u => u.ValueKind != JsonValueKind.String || !RedirectUris.IsAllowed(u.GetString()!)))
            {
                return (null, $"needs 1 to {MaxRedirectUris} redirect_uris, each https or http on a loopback address");
            }
            var host = new Uri(clientId).Host;
            return (new RegisteredClient
            {
                ClientId = clientId,
                ClientName = root.TryGetProperty("client_name", out var name) && name.ValueKind == JsonValueKind.String
                    ? CleanName(name.GetString(), host)
                    : host,
                RedirectUris = uris.EnumerateArray().Select(u => u.GetString()!).ToList(),
                AuthMethod = ClientAuthMethods.None,
                Created = now,
            }, null);
        }
    }

    /// <summary>A client's own name, made safe to show: no control characters, at most <see cref="MaxNameLength"/> characters.</summary>
    public static string CleanName(string? name, string fallback)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length is > 0 and <= MaxNameLength ? clean : clean.Length > MaxNameLength ? clean[..MaxNameLength] : fallback;
    }

    /// <summary>An absent list is fine (the defaults are the code flow); a present one must include <paramref name="value"/>.</summary>
    private static bool Contains(JsonElement root, string property, string value) =>
        !root.TryGetProperty(property, out var list)
        || (list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == value));
}
