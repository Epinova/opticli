using System.Diagnostics;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class SiteProcessTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData("setsid")]
    [InlineData("perl")]
    public async Task A_detached_site_runs_in_a_session_of_its_own(string detacher)
    {
        // /proc gives the session id; the perl route is what macOS uses, but only Linux can check it here.
        var tool = detacher == "setsid" ? "/usr/bin/setsid" : "/usr/bin/perl";
        if (!OperatingSystem.IsLinux() || !File.Exists(tool) || !File.Exists("/bin/sleep"))
        {
            return;
        }
        var log = _root.Combine("site.log");
        // A stand-in for `dotnet Site.dll`: writes to the log, then waits to be stopped.
        var site = _root.Write("site.sh", "echo started\nexec /bin/sleep 30\n");
        var info = SiteProcess.DetachedUnix("/bin/sh", site, log, detacher == "setsid" ? tool : null, detacher == "perl" ? tool : null);
        info.UseShellExecute = false;
        using var process = Process.Start(info)!;
        try
        {
            await WaitForAsync(() => File.Exists(log) && File.ReadAllText(log).Contains("started", StringComparison.Ordinal));
            // The script logs before its own exec, so the shell may not have become sleep yet: wait for that too.
            await WaitForAsync(() => File.ReadAllText($"/proc/{process.Id}/comm").Trim() == "sleep");

            // The process started is the site itself (everything execs), and it leads its own session.
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            Assert.Equal(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[3]);
        }
        finally
        {
            // No shutdown endpoint to ask: SIGTERM, which sleep obeys.
            Assert.True(await SiteProcess.StopAsync(process, TimeSpan.FromSeconds(5), () => Task.FromResult(false)));
        }
    }

    [Fact]
    public async Task Stop_asks_through_the_shutdown_request_before_anything_else()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sleep"))
        {
            return;
        }
        using var process = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        var asked = 0;

        // Accepted but ignored: the kill follows after the grace period.
        var graceful = await SiteProcess.StopAsync(process, TimeSpan.FromMilliseconds(200), () =>
        {
            asked++;
            return Task.FromResult(true);
        });

        Assert.Equal(1, asked);
        Assert.False(graceful);
        Assert.True(process.HasExited);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "timed out");
            await Task.Delay(50);
        }
    }
}
