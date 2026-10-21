using System.Diagnostics;

namespace OptiCli.Core.Discovery;

/// <summary>
/// Runs <c>git</c> for the few short things opticli asks it (drift's history lookup has its own runner, with a time
/// budget); never fails when git isn't there.
/// </summary>
public static class Git
{
    /// <summary>
    /// Whether <paramref name="directory"/> or a folder above it has a <c>.git</c> (folder, or file for a worktree or
    /// submodule). git isn't run otherwise: on macOS without the command line tools, <c>git</c> offers to install them.
    /// </summary>
    public static bool InRepository(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var dotGit = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return true;
            }
        }
        return false;
    }

    /// <returns>The exit code and standard output; null outside a git work tree, when git can't be run, or after 10 s.</returns>
    public static (int ExitCode, string Output)? Run(string directory, params string[] args)
    {
        if (!InRepository(directory))
        {
            return null;
        }
        try
        {
            var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }
            // Never ask anything, and take no lock a running git (an IDE's) would wait on.
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return (process.ExitCode, output.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <returns>Standard output when git exits with 0; otherwise null.</returns>
    public static string? Output(string directory, params string[] args) =>
        Run(directory, args) is (0, var output) ? output : null;
}
