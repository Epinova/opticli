using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OptiCli.Core.Text;

/// <summary>
/// Host names as a site definition stores them (<c>tblHostDefinition.Name</c>): <c>name[:port]</c>, or <c>*</c> for the
/// host that answers every name no other site has. Dependency-free, so the CLI and the agent read them the same way.
/// </summary>
public static class HostNames
{
    /// <summary>The wildcard host (<c>HostDefinition.WildcardHostName</c>).</summary>
    public const string Wildcard = "*";

    public const string Syntax = "name[:port] (localhost:5001), or a URL with nothing after the host (https://localhost:5001/)";

    /// <summary>
    /// <c>localhost:5001</c> as it is; <c>https://localhost:5001/</c> as <c>localhost:5001</c> with <paramref name="https"/>
    /// true (false for <c>http://</c>, null without a scheme). Lower case, without a trailing <c>/</c>, and without the
    /// default port of the scheme in effect (see the remarks).
    /// </summary>
    /// <remarks>
    /// A browser leaves the scheme's default port out of the Host header, and <see cref="Uri"/> out of a URL (SiteUrl,
    /// whose host the CMS adds to the site when it isn't there), so <c>name:443</c> over https would never match a
    /// request. The scheme in effect is <paramref name="scheme"/> when given, else the text's own; only that scheme's
    /// default port goes (<c>http://localhost:443</c> keeps its port). With neither, the port says the scheme: 443 is
    /// https, 80 is http.
    /// </remarks>
    /// <param name="scheme">The https setting the host gets (<c>--https</c>, or a request's <c>https</c>), which overrides the text's scheme; null: none given.</param>
    /// <param name="error">Why <paramref name="input"/> isn't a host name, when it isn't.</param>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? name, out bool? https, [NotNullWhen(false)] out string? error, bool? scheme = null) =>
        TryNormalize(input, scheme, out name, out https, out error);

    /// <summary>
    /// The host as typed, only without a scheme and a trailing <c>/</c>, lower case: to find a host a site has under a
    /// name <see cref="TryNormalize"/> would change (<c>www.site.example:443</c>, from admin mode or SQL).
    /// </summary>
    public static string Literal(string input)
    {
        var text = input.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }
        return (text.EndsWith('/') ? text[..^1] : text).ToLowerInvariant();
    }

    private static bool TryNormalize(string? input, bool? effective, [NotNullWhen(true)] out string? name, out bool? https, [NotNullWhen(false)] out string? error)
    {
        name = null;
        https = null;
        error = null;
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = "The host name is empty.";
            return false;
        }
        if (text.Any(char.IsWhiteSpace))
        {
            error = $"'{text}' has whitespace in it; a host name is {Syntax}.";
            return false;
        }
        if (text == Wildcard)
        {
            name = Wildcard;
            return true;
        }

        var rest = text;
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var given = rest[..scheme];
            if (given.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                https = true;
            }
            else if (given.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                https = false;
            }
            else
            {
                error = $"'{text}' has the scheme '{given}'; a site's host is reached over http or https.";
                return false;
            }
            rest = rest[(scheme + 3)..];
        }
        if (rest.EndsWith('/'))
        {
            rest = rest[..^1];
        }
        if (rest.IndexOfAny(['/', '?', '#', '\\']) >= 0)
        {
            error = $"'{text}' has a path, query or fragment; a host name is {Syntax}.";
            return false;
        }
        if (rest.Contains('@'))
        {
            error = $"'{text}' has user info in it; a host name is {Syntax}.";
            return false;
        }

        // name or name:port. The CMS splits a host on ':' (UriAuthority), so it can't store an IPv6 address.
        if (rest.StartsWith('['))
        {
            error = $"'{text}' is an IPv6 address, which the CMS can't store as a site host; use localhost (or 127.0.0.1) instead.";
            return false;
        }
        var colon = rest.IndexOf(':');
        if (colon >= 0 && rest.IndexOf(':', colon + 1) >= 0)
        {
            error = $"'{text}' has more than one ':'; a host name is {Syntax}.";
            return false;
        }
        var host = colon < 0 ? rest : rest[..colon];
        var port = colon < 0 ? null : rest[(colon + 1)..];

        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = $"'{text}' is not a valid host name; a host name is {Syntax}.";
            return false;
        }
        int? number = null;
        if (port is not null)
        {
            if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 65535)
            {
                error = $"'{text}' has the port '{port}'; a port is a number from 1 to 65535.";
                return false;
            }
            number = parsed;
        }

        if (effective is not null)
        {
            https = effective;
        }
        if ((number == 443 && https != false) || (number == 80 && https != true))
        {
            https ??= number == 443;
            number = null;
        }

        name = (number is null ? host : $"{host}:{number.Value.ToString(CultureInfo.InvariantCulture)}").ToLowerInvariant();
        return true;
    }

    /// <summary>Host names are compared case-insensitively, as DNS and the CMS's own URL matching do.</summary>
    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="name"/> only works on this machine (<c>localhost</c>, <c>*.localhost</c>, <c>*.test</c>, a
    /// loopback address): anything else is taken for a real, probably production, host name.
    /// </summary>
    public static bool IsLocal(string name)
    {
        var host = HostPart(name).ToLowerInvariant();
        return host is "localhost"
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".test", StringComparison.Ordinal)
            || host.StartsWith("127.", StringComparison.Ordinal);
    }

    /// <summary>
    /// The site URL for a primary host: <c>https://</c> unless the host's flag says http, the host, and the path of the
    /// <paramref name="current"/> site URL (the site's application path; <c>/</c> without one). Printed as
    /// <see cref="Uri"/> prints it, which is how the CMS stores and returns it.
    /// </summary>
    public static string SiteUrl(string name, bool? https, string? current = null)
    {
        var path = Uri.TryCreate(current, UriKind.Absolute, out var old) ? old.AbsolutePath : "/";
        return new Uri($"{(https == false ? "http" : "https")}://{name}{path}").ToString();
    }

    /// <summary>
    /// The host a site URL names (<c>Uri.Authority</c>: <c>name[:port]</c> without the scheme's default port), as the CMS
    /// adds it to the site; null when it isn't an absolute URL.
    /// </summary>
    public static string? FromUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority.ToLowerInvariant() : null;

    private static string HostPart(string name)
    {
        var colon = name.IndexOf(':');
        return colon < 0 ? name : name[..colon];
    }
}
