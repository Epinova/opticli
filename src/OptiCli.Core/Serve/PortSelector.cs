using System.Net;
using System.Net.Sockets;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Serve;

/// <summary>Picks the loopback port the site listens on.</summary>
public static class PortSelector
{
    public const int DefaultPort = 5199;
    public const int RangeStart = 5199;
    public const int RangeEnd = 5299;

    /// <param name="explicitPort"><c>--port</c>: used as given, or an error when taken.</param>
    /// <param name="configuredPort">The user config's <c>port</c>: preferred, but another free one is fine.</param>
    /// <param name="isFree">Probe; <see cref="IsFree"/> outside tests.</param>
    /// <exception cref="UsageException">The explicit port is taken, or the whole range is.</exception>
    public static int Select(int? explicitPort, int? configuredPort, Func<int, bool> isFree)
    {
        if (explicitPort is { } port)
        {
            if (port is < 1 or > 65535)
            {
                throw new UsageException($"--port {port} is not a valid port.");
            }
            return isFree(port)
                ? port
                : throw new UsageException($"Port {port} is already in use.", $"Pick another with --port, or leave it out to use the first free port in {RangeStart}-{RangeEnd}.");
        }

        var preferred = configuredPort ?? DefaultPort;
        if (isFree(preferred))
        {
            return preferred;
        }
        for (var candidate = RangeStart; candidate <= RangeEnd; candidate++)
        {
            if (candidate != preferred && isFree(candidate))
            {
                return candidate;
            }
        }
        throw new UsageException($"No free port in {RangeStart}-{RangeEnd}.", "Stop other sites, or pass --port.");
    }

    /// <summary>True when nothing listens on 127.0.0.1:<paramref name="port"/> (the address the site binds).</summary>
    public static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
