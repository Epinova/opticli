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
    /// session on Unix, with all output appended to <paramref name="logPath"/>.
    /// </summary>
    /// <remarks>
    /// On Unix the new session comes from <c>setsid</c> (Linux) or, where there is none (macOS), Perl's
    /// <c>POSIX::setsid</c>; so neither Ctrl+C in opticli's terminal nor a signal to opticli's process group reaches
    /// the site. Both exec the shell, which execs the site, and setsid only forks when it is a process group leader (a
    /// child started by .NET never is), so the returned process is the site itself. Its stdio points at the log
    /// and /dev/null, never at this process's pipes, so a caller capturing opticli's output isn't held open.
    /// On Windows the returned process is the <c>cmd.exe</c> that runs the site, in a console of its own.
    /// </remarks>
    /// <param name="environment">Set over the inherited environment, in order; a null value removes the variable.</param>
    public static Process StartDetached(string dotnet, string dll, string workingDirectory, IEnumerable<KeyValuePair<string, string?>> environment, string logPath)
    {
        var info = OperatingSystem.IsWindows()
            // cmd has its own quoting rules, so the command line is written out rather than escaped per argument.
            ? new ProcessStartInfo("cmd.exe", $"/d /c \"\"{dotnet}\" \"{dll}\" >> \"{logPath}\" 2>&1 < NUL\"") { CreateNoWindow = true }
            : DetachedUnix(dotnet, dll, logPath, FirstExisting("/usr/bin/setsid", "/bin/setsid"), FirstExisting("/usr/bin/perl", "/bin/perl"));
        return Start(info, workingDirectory, environment);
    }

    /// <summary>The Unix command line of <see cref="StartDetached"/>, with the detaching tools found (null: missing).</summary>
    internal static ProcessStartInfo DetachedUnix(string dotnet, string dll, string logPath, string? setsid, string? perl)
    {
        ProcessStartInfo info;
        if (setsid is not null)
        {
            info = new ProcessStartInfo(setsid);
        }
        else if (perl is not null)
        {
            info = new ProcessStartInfo(perl);
            info.ArgumentList.Add("-MPOSIX");
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("POSIX::setsid(); exec { $ARGV[0] } @ARGV or die \"opticli: cannot start $ARGV[0]: $!\\n\";");
            info.ArgumentList.Add("--");
        }
        else
        {
            info = new ProcessStartInfo("/bin/sh");
        }
        if (info.FileName != "/bin/sh")
        {
            info.ArgumentList.Add("/bin/sh");
        }
        info.ArgumentList.Add("-c");
        // Without a new session, at least ignore the terminal's hangup.
        info.ArgumentList.Add($"{(setsid is null && perl is null ? "trap '' HUP; " : "")}log=$1; shift; exec \"$@\" >>\"$log\" 2>&1 </dev/null");
        info.ArgumentList.Add("opticli-site");
        info.ArgumentList.Add(logPath);
        info.ArgumentList.Add(dotnet);
        info.ArgumentList.Add(dll);
        return info;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    /// <summary>Starts the site as a child whose output the caller reads (foreground mode).</summary>
    /// <param name="redirectInput">Give the site a pipe as stdin, which the caller writes to (the sealed apply's secrets).</param>
    public static Process StartAttached(string dotnet, string dll, string workingDirectory, IEnumerable<KeyValuePair<string, string?>> environment, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
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

    /// <summary>
    /// For a sealed run: the host of the .NET installation opticli runs on (a global tool runs from its own launcher, so
    /// <see cref="DotnetHost"/> would be the first <c>dotnet</c> on PATH), when it has every framework the site's
    /// <c>runtimeconfig.json</c> asks for, in its major version; otherwise <see cref="DotnetHost"/>.
    /// </summary>
    public static string OwnDotnetHost(string siteDll)
    {
        var current = DotnetHost();
        if (current != "dotnet")
        {
            return current;
        }
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var frameworks = Frameworks(Path.ChangeExtension(siteDll, ".runtimeconfig.json"));
        return File.Exists(host) && frameworks is { Count: > 0 } && frameworks.All(f => Has(root, f.Name, f.Major)) ? host : current;
    }

    private static bool Has(string root, string framework, string major)
    {
        var folder = Path.Combine(root, "shared", framework);
        return Directory.Exists(folder) && Directory.EnumerateDirectories(folder, $"{major}.*").Any();
    }

    /// <summary>The frameworks a runtimeconfig.json names, with their major versions; null when it can't be read.</summary>
    internal static IReadOnlyList<(string Name, string Major)>? Frameworks(string runtimeConfig)
    {
        try
        {
            using var stream = File.OpenRead(runtimeConfig);
            var options = System.Text.Json.Nodes.JsonNode.Parse(stream)?["runtimeOptions"];
            var listed = options?["frameworks"]?.AsArray().ToList() ?? (options?["framework"] is { } one ? [one] : []);
            return listed
                .Select(f => (Name: f?["name"]?.GetValue<string>(), Version: f?["version"]?.GetValue<string>()))
                .Where(f => !string.IsNullOrEmpty(f.Name) && !string.IsNullOrEmpty(f.Version))
                .Select(f => (f.Name!, f.Version!.Split('.')[0]))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
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

    /// <summary>
    /// Asks the site to shut down, through <paramref name="requestShutdown"/> (the agent's shutdown endpoint) or else
    /// SIGTERM on Unix, then kills it (and what it started) if it hasn't exited within <paramref name="grace"/>.
    /// On Windows, with no endpoint to ask, that is a kill straight away.
    /// </summary>
    /// <param name="requestShutdown">True when the site accepted the request.</param>
    /// <returns>True when it exited gracefully.</returns>
    public static async Task<bool> StopAsync(Process process, TimeSpan grace, Func<Task<bool>>? requestShutdown = null)
    {
        if (process.HasExited)
        {
            return true;
        }
        var asked = requestShutdown is not null && await requestShutdown();
        if (!asked && !OperatingSystem.IsWindows())
        {
            asked = Kill(process.Id, SigTerm) == 0;
        }
        if (asked)
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
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited just now.
        }
        await process.WaitForExitAsync();
        return false;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}
