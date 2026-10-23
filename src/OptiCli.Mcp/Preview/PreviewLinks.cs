using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EPiServer.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Preview;

/// <summary>What an assistant's preview link stands for: one version, in one language, for one editor's connection.</summary>
/// <param name="Version">The content version, <c>123_456</c> (with the provider's name, if any).</param>
/// <param name="Language">The version's language; empty for content that has none.</param>
internal sealed record PreviewLink(string GrantId, string UserName, string Version, string Language, DateTimeOffset Issued, DateTimeOffset Expires);

/// <summary>
/// The assistant's own links to a draft (<see cref="OptiCliMcpOptions.AllowPreviewLinks"/>):
/// <c>{origin}{BasePath}/preview/{payload}</c>, where the payload is the grant, the editor, the version and its
/// language, protected and time-limited with the site's Data Protection keys, as the access tokens are. Nothing in the
/// request but the payload picks what is shown (<see cref="PreviewLinkMiddleware"/>), and the payload carries no
/// credential that works anywhere else: never the CMS's preview token.
/// </summary>
internal sealed class PreviewLinks(IDataProtectionProvider protection, IOptions<OptiCliMcpOptions> options, TimeProvider time)
{
    /// <summary>The path segment below <see cref="OptiCliMcpOptions.BasePath"/>.</summary>
    public const string Segment = "/preview";

    /// <summary>Longer than any link the module makes; anything longer isn't read.</summary>
    internal const int MaxLength = 2048;

    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("OptiCli.Mcp.PreviewLink.v1").ToTimeLimitedDataProtector();

    private sealed record Payload(
        [property: JsonPropertyName("g")] string GrantId,
        [property: JsonPropertyName("u")] string UserName,
        [property: JsonPropertyName("v")] string Version,
        [property: JsonPropertyName("l")] string Language,
        [property: JsonPropertyName("iat")] long Issued,
        [property: JsonPropertyName("n")] string Nonce);

    /// <summary><c>{BasePath}/preview</c>: every link is one segment below it.</summary>
    public static string Path(OptiCliMcpOptions options) => options.BasePath + Segment;

    /// <summary>A link to <paramref name="version"/> for the editor <paramref name="userName"/> of <paramref name="grantId"/>, on the MCP request's origin.</summary>
    /// <returns>The link, and when it stops working (<see cref="OptiCliMcpOptions.PreviewLinkLifetime"/> from now).</returns>
    public (string Url, DateTimeOffset Expires) Issue(HttpRequest request, string grantId, string userName, string version, string? language)
    {
        var now = time.GetUtcNow();
        var expires = now.Add(options.Value.PreviewLinkLifetime);
        // The nonce makes every link unique, so one can't be told from another by its look.
        var payload = new Payload(grantId, userName, version, language ?? "", now.ToUnixTimeSeconds(), Secrets.Base64Url(RandomNumberGenerator.GetBytes(16)));
        var token = _protector.Protect(JsonSerializer.Serialize(payload), expires);
        return ($"{McpUrls.Origin(request)}{Path(options.Value)}/{token}", expires);
    }

    /// <returns>What the link's payload stands for; null for one that is malformed, tampered with, expired or not a preview link.</returns>
    public PreviewLink? Read(string token)
    {
        if (token.Length is 0 or > MaxLength)
        {
            return null;
        }
        Payload? payload;
        DateTimeOffset expires;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token, out expires));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
        if (payload is not { GrantId.Length: > 0, UserName.Length: > 0 } || payload.Language is null
            || !ContentReference.TryParse(payload.Version, out var version) || version.WorkID <= 0)
        {
            return null;
        }
        return new PreviewLink(payload.GrantId, payload.UserName, payload.Version, payload.Language, DateTimeOffset.FromUnixTimeSeconds(payload.Issued), expires);
    }
}
