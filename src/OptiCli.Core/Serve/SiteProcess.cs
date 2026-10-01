using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OptiCli.Core.Serve;

/// <summary>Starting, finding and stopping the site process.</summary>
public static class SiteProcess
{
    private const int SigTerm = 15;

    /// <summary>Tolerance when matching a process's start time with the recorded one.</summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starts <c>dotnet &lt;dll&gt;</c> so that it outlives this CLI process and its terminal: in a new
    /// session (<c>setsid</c>) on Unix, with all output appended to <paramref name="logPath"/>.
    /// </summary>
    /// <remarks>
    /// The shell execs the site, and <c>setsid</c> only forks when it is a process group leader (a child
    /// started by .NET never is), so the returned process is the site itself. Its stdio points at the log
    /// and /dev/null, never at this process's pipes, so a caller capturing opticli's output isn't held open.
    /// </remarks>
    /// <param name="environment">Set over the inherited environment, in order; a null value removes the variable.</param>
    public static Process StartDetached(string dotnet, string dll, string workingDirectory, IEnumerable<KeyValuePair<string, string?>> environment, string logPath)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            // cmd has its own quoting rules, so the command line is written out rather than escaped per argument.
            info = new ProcessStartInfo("cmd.exe", $"/d /c \"\"{dotnet}\" \"{dll}\" >> \"{logPath}\" 2>&1 < NUL\"") { CreateNoWindow = true };
        }
        else
        {
            var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
            info = new ProcessStartInfo(setsid ?? "/bin/sh");
            if (setsid is not null)
            {
                info.ArgumentList.Add("/bin/sh");
            }
            info.ArgumentList.Add("-c");
            // Without setsid, at least ignore the terminal's hangup.
            info.ArgumentList.Add($"{(setsid is null ? "trap '' HUP; " : "")}log=$1; shift; exec \"$@\" >>\"$log\" 2>&1 </dev/null");
            info.ArgumentList.Add("opticli-site");
            info.ArgumentList.Add(logPath);
            info.ArgumentList.Add(dotnet);
            info.ArgumentList.Add(dll);
        }
        return Start(info, workingDirectory, environment);
    }

    /// <summary>Starts the site as a child whose output the caller reads (foreground mode).</summary>
    public static Process StartAttached(string dotnet, string dll, string workingDirectory, IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var info = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(dll);
        return Start(info, workingDirectory, environment);
    }

    private static Process Start(ProcessStartInfo info, string workingDirectory, IEnumerable<KeyValuePair<string, string?>> environment)
    {
        info.UseShellExecute = false;
        info.WorkingDirectory = workingDirectory;
        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }
        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {info.FileName}.");
    }

    /// <summary>The <c>dotnet</c> host: the one running opticli when it runs as <c>dotnet opticli.dll</c>, else the one on PATH.</summary>
    public static string DotnetHost()
    {
        var current = Environment.ProcessPath;
        return current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? current
            : "dotnet";
    }

    public static DateTimeOffset? StartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The recorded site process, if it still runs (a reused pid with another start time doesn't count).</summary>
    public static Process? Find(int pid, DateTimeOffset? recordedStart)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (process.HasExited || (recordedStart is { } expected && StartTime(process) is { } actual && (actual - expected).Duration() > StartTimeTolerance))
        {
            process.Dispose();
            return null;
        }
        return process;
    }

    /// <summary>Asks the site to shut down (SIGTERM on Unix), then kills it if it hasn't exited in time.</summary>
    /// <returns>True when it exited gracefully.</returns>
    public static async Task<bool> StopAsync(Process process, TimeSpan grace)
    {
        if (process.HasExited)
        {
            return true;
        }
        if (!OperatingSystem.IsWindows() && Kill(process.Id, SigTerm) == 0)
        {
            using var timeout = new CancellationTokenSource(grace);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Fall through to a hard kill.
            }
        }
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        return false;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}
