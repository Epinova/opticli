using System.Globalization;
using OptiCli.Core.Cms;
using OptiCli.Core.Text;

namespace OptiCli.Core.Serve;

/// <summary>
/// Whether any of the database's sites answers on the addresses <c>serve</c> listens on. The CMS picks the site of a
/// request by its host (<c>name[:port]</c>, the default port left out): a site answers on a host it has, and the site
/// with the <c>*</c> host (CMS 13: the default application) on every host no other site has. With none of those, every
/// page on serve's address is a 404, which looks like a broken site rather than a database restored with production
/// host names, or hosts set for another port (a launch profile's <c>applicationUrl</c>, which serve doesn't use).
/// </summary>
public static class SiteHostMatch
{
    /// <summary>How many hosts the warning names before it says how many more there are.</summary>
    private const int MaxListed = 6;

    /// <summary>
    /// A warning when no site answers on serve's addresses: <c>127.0.0.1</c> or <c>localhost</c> on
    /// <paramref name="httpPort"/>, <c>localhost</c> on <paramref name="httpsPort"/>. Null when one does, or when the
    /// database has no sites yet (the CMS makes one on the first request).
    /// </summary>
    /// <remarks>
    /// A redirect host doesn't count: it sends the browser to the site's primary host. A host on <c>localhost</c> or
    /// <c>127.0.0.1</c> with another port is named in the hint as the <c>--port</c> (or, with https,
    /// <c>--https-port</c>) to start serve on instead.
    /// </remarks>
    public static string? Warning(IReadOnlyList<SiteInfo> sites, int httpPort, int? httpsPort)
    {
        if (sites.Count == 0 || sites.Any(Answers(httpPort, httpsPort)))
        {
            return null;
        }
        var served = $"{SiteEnvironment.Url(httpPort)}{(httpsPort is { } https ? $" and {SiteEnvironment.HttpsUrl(https)}" : "")}";
        var hosts = sites.SelectMany(s => s.Hosts.Where(h => !IsRedirect(h.Type)).Select(h => $"{h.Name} ({s.Name})")).Distinct().ToList();
        var listed = hosts.Count == 0
            ? "no hosts"
            : string.Join(", ", hosts.Take(MaxListed)) + (hosts.Count > MaxListed ? $" and {hosts.Count - MaxListed} more" : "");
        var local = sites.SelectMany(s => s.Hosts.Where(h => !IsRedirect(h.Type)).Select(h => (Site: s, Host: h)))
            .Select(p => (p.Site, p.Host, Port: LocalPort(p.Host.Name)))
            .Where(p => p.Port is not null)
            .Select(p => p.Host.Https == true || (p.Host.Https is null && p.Site.Url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
                ? $"--https-port {p.Port!.Value.ToString(CultureInfo.InvariantCulture)}"
                : $"--port {p.Port!.Value.ToString(CultureInfo.InvariantCulture)}")
            .Distinct()
            .ToList();
        var site = sites[0].Name.Contains(' ', StringComparison.Ordinal) || sites[0].Name.Contains('"', StringComparison.Ordinal)
            ? $"\"{sites[0].Name.Replace("\"", "\\\"", StringComparison.Ordinal)}=localhost:{httpPort.ToString(CultureInfo.InvariantCulture)}\""
            : $"{sites[0].Name}=localhost:{httpPort.ToString(CultureInfo.InvariantCulture)}";
        return $"No site of the database answers on {served}, so every page there is a 404: the sites have {listed}, and none has the * host. "
            + (local.Count > 0
                ? $"Start serve on the port a site has ({string.Join(" or ", local)}), or point a site at this address: "
                : "Point a site at this address: ")
            + $"opticli sites primary {site} (see \"Site hosts after a database restore\" in the reference).";
    }

    /// <summary>Whether the site answers on serve's addresses: a host there, the * host, or (CMS 13) the default application.</summary>
    private static Func<SiteInfo, bool> Answers(int httpPort, int? httpsPort)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"127.0.0.1:{httpPort.ToString(CultureInfo.InvariantCulture)}",
            $"localhost:{httpPort.ToString(CultureInfo.InvariantCulture)}",
        };
        if (httpsPort is { } https)
        {
            addresses.Add(https == 443 ? "localhost" : $"localhost:{https.ToString(CultureInfo.InvariantCulture)}");
        }
        if (httpPort == 80)
        {
            addresses.Add("127.0.0.1");
            addresses.Add("localhost");
        }
        return site => site.IsDefault == true
            || site.Hosts.Any(h => !IsRedirect(h.Type) && (h.Name.Trim() == HostNames.Wildcard || addresses.Contains(h.Name.Trim())))
            || (site.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && addresses.Contains(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}"));
    }

    private static bool IsRedirect(HostType type) => type is HostType.RedirectPermanent or HostType.RedirectTemporary;

    /// <summary>The port of a host on this machine (<c>localhost:9299</c>, <c>127.0.0.1:5001</c>); null for any other host.</summary>
    private static int? LocalPort(string host)
    {
        var colon = host.LastIndexOf(':');
        if (colon < 0)
        {
            return null;
        }
        var name = host[..colon].Trim();
        return (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1")
            && int.TryParse(host[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : null;
    }
}
