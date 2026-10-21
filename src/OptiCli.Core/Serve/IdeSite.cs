using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using OptiCli.Protocol;

namespace OptiCli.Core.Serve;

/// <summary>
/// A site the developer started from the IDE with <c>opticli ide on</c>: its agent's announcement, and how to reach it
/// on one of the site's own addresses.
/// </summary>
public static partial class IdeSite
{
    /// <summary>The extension the ASP.NET Core HTTPS development certificate carries (<c>dotnet dev-certs https</c>).</summary>
    public const string DevelopmentCertificateOid = "1.3.6.1.4.1.311.84.1.1";

    /// <summary>
    /// The announcement of a site that still runs, as a state the probe can use; null when there is none. An announcement
    /// whose process is gone (the IDE's stop kills the site) or that can't be read is removed. Its
    /// <see cref="ServeState.AgentUrl"/> is null when the site listens on no address of this machine.
    /// </summary>
    public static ServeState? Running(StateStore store)
    {
        if (Read(store.IdePath) is not { } announcement)
        {
            return null;
        }
        using (var process = SiteProcess.Find(announcement.ProcessId, announcement.ProcessStartTime))
        {
            if (process is null)
            {
                Forget(store.IdePath, announcement.Token);
                return null;
            }
        }
        var url = AgentUrl(announcement.Addresses);
        return new ServeState(
            ServeMode.Ide,
            store.ProjectDirectory,
            url?.Port ?? 0,
            announcement.Token,
            announcement.StartedAt,
            store.LogPath,
            announcement.DatabaseServer,
            announcement.DatabaseName,
            announcement.ProcessId,
            announcement.ProcessStartTime,
            AgentDll: null,
            AgentUrl: url);
    }

    /// <summary>The announcement in <paramref name="path"/>; null when there is none, and when it is unreadable (it is removed then).</summary>
    internal static IdeAnnouncement? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            if (JsonSerializer.Deserialize<IdeAnnouncement>(File.ReadAllText(path), AgentJson.Options) is { Token: not null, Addresses: not null } announcement)
            {
                return announcement;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The site rewrites it at its next start.
        }
        TryDelete(path);
        return null;
    }

    /// <summary>Removes the announcement while it is still the one with <paramref name="token"/>: the site may have restarted meanwhile.</summary>
    private static void Forget(string path, string token)
    {
        try
        {
            if (JsonSerializer.Deserialize<IdeAnnouncement>(File.ReadAllText(path), AgentJson.Options)?.Token == token)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Where to reach the agent among the addresses the site listens on: plain HTTP on a loopback or wildcard host first
    /// (it skips the site's HTTPS redirect, which runs after the agent anyway), else HTTPS on <c>localhost</c>, the name the
    /// development certificate is issued for. A name below <c>.localhost</c> counts as this machine. Addresses on other
    /// host names are never used: the token goes to this machine only.
    /// </summary>
    /// <returns>Null when none of them is on this machine.</returns>
    public static Uri? AgentUrl(IEnumerable<string> addresses)
    {
        var usable = addresses.Select(Usable).OfType<Uri>().ToList();
        return usable.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttp) ?? usable.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttps);
    }

    private static Uri? Usable(string address)
    {
        var match = AddressPattern().Match(address.Trim());
        if (!match.Success)
        {
            return null;
        }
        var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        var host = match.Groups["host"].Value;
        var port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture) : scheme == "https" ? 443 : 80;
        string? target;
        // A name below .localhost (site.dev.localhost) is this machine too (RFC 6761), and Kestrel binds it to loopback. opticli
        // connects to localhost itself rather than resolve the name: the token never depends on a resolver.
        if (host is "*" or "+" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            target = scheme == "https" ? "localhost" : "127.0.0.1";
        }
        else if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            target = ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
                ? scheme == "https" ? "localhost" : "127.0.0.1"
                : IPAddress.IsLoopback(ip) ? host : null;
        }
        else
        {
            target = null;
        }
        return target is null || port is <= 0 or > 65535 ? null : new Uri($"{scheme}://{target}:{port}");
    }

    [GeneratedRegex(@"^(?<scheme>https?)://(?<host>\[[^\]]+\]|[^:/\[\]]+)(:(?<port>\d{1,5}))?/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    /// <summary>
    /// Accepts a certificate that validates, or the ASP.NET Core development certificate on a loopback address: on Linux
    /// OpenSSL doesn't trust that one even after <c>dotnet dev-certs https --trust</c>, and an IDE's site is often HTTPS only.
    /// </summary>
    public static bool AcceptCertificate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None
        || (certificate is not null && request.RequestUri is { } uri && IsLoopback(uri.Host) && IsDevelopmentCertificate(certificate));

    internal static bool IsDevelopmentCertificate(X509Certificate2 certificate)
    {
        var now = DateTime.Now;
        return certificate.NotBefore <= now && now <= certificate.NotAfter
            && certificate.Extensions.Any(e => e.Oid?.Value == DevelopmentCertificateOid);
    }

    internal static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
