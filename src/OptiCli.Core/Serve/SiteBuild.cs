using System.Diagnostics;
using OptiCli.Core.Discovery;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Serve;

/// <summary><c>serve --build</c>: <c>dotnet build</c> of the site project before starting it.</summary>
public static class SiteBuild
{
    /// <param name="output">Where the build's output goes (stderr, so stdout stays one JSON envelope).</param>
    /// <exception cref="UsageException">The build failed.</exception>
    public static async Task RunAsync(ProjectInfo project, TextWriter output, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(SiteProcess.DotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = project.Directory,
        };
        info.ArgumentList.Add("build");
        info.ArgumentList.Add(project.ProjectFile);
        info.ArgumentList.Add("--nologo");

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start dotnet build.");
        process.OutputDataReceived += (_, e) => Write(output, e.Data);
        process.ErrorDataReceived += (_, e) => Write(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new UsageException($"dotnet build {Path.GetFileName(project.ProjectFile)} failed (exit code {process.ExitCode}).", "Fix the build errors shown above, then run `opticli serve` again.");
        }
    }

    private static void Write(TextWriter output, string? line)
    {
        if (line is not null)
        {
            lock (output)
            {
                output.WriteLine(line);
            }
        }
    }
}
