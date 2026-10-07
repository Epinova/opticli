using System.Diagnostics;
using System.Globalization;

namespace OptiCli.Core.Serve;

/// <summary>
/// Claims for ports a starting <c>serve</c> has picked but its site doesn't listen on yet, so two <c>serve</c>s started at
/// the same moment (for different projects, or with separate state) don't pick the same free port and one of the sites
/// then fail to bind. A claim is a file per port in this user's temp folder holding the claiming process's id; it counts
/// while that process runs (a background <c>serve</c> exits once its site listens, which then holds the port itself).
/// Best effort: when the folder can't be used, every port counts as unclaimed, as before.
/// </summary>
public sealed class PortClaims(string directory)
{
    /// <summary>A claim older than this doesn't count, whatever process has the id now.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    public static PortClaims ForUser { get; } = new(Path.Combine(Path.GetTempPath(), $"opticli-ports-{Environment.UserName}"));

    /// <param name="isFree">Whether nothing listens on the port (<see cref="PortSelector.IsFree"/>).</param>
    /// <returns>A probe for <see cref="PortSelector.Select"/>: free, and now claimed by this process.</returns>
    public Func<int, bool> FreeAndClaimed(Func<int, bool> isFree) => port => isFree(port) && TryClaim(port);

    /// <summary>Claims <paramref name="port"/> for this process, unless another running process has a claim on it.</summary>
    public bool TryClaim(int port) => TryClaim(port, Environment.ProcessId, IsRunning, DateTimeOffset.UtcNow);

    internal bool TryClaim(int port, int processId, Func<int, bool> isRunning, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, port.ToString(CultureInfo.InvariantCulture));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new StreamWriter(stream);
                    writer.Write(processId.ToString(CultureInfo.InvariantCulture));
                    return true;
                }
                catch (IOException) when (File.Exists(file))
                {
                    var age = now - File.GetLastWriteTimeUtc(file);
                    string text;
                    try
                    {
                        text = File.ReadAllText(file);
                    }
                    catch (IOException)
                    {
                        // Another process is writing its claim right now.
                        return false;
                    }
                    var owner = int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
                    if (owner == processId)
                    {
                        return true;
                    }
                    if (owner != 0 && isRunning(owner) && age < MaxAge)
                    {
                        return false;
                    }
                    // A stale claim (its process is gone, or it is old): take it over.
                    File.Delete(file);
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
