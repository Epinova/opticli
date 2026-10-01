using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using OptiCli.Cli;

namespace OptiCli.Tests;

/// <summary>Tests that swap <see cref="Console.Out"/> and <see cref="Console.Error"/> run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}

/// <summary>What one run of opticli printed and returned.</summary>
internal sealed record Run(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>stdout as the single JSON document it should be.</summary>
    public JsonElement Json()
    {
        using var document = JsonDocument.Parse(Stdout);
        return document.RootElement.Clone();
    }

    public JsonElement Error() => Json().GetProperty("error");

    public override string ToString() => $"exit {ExitCode}\nstdout: {Stdout}\nstderr: {Stderr}";
}

/// <summary>Runs opticli: in this process through its entry point, or as a child process with redirected output.</summary>
internal static class Opticli
{
    private static readonly MethodInfo EntryPoint =
        typeof(Program).GetMethod("<Main>$", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("opticli's entry point was not found.");

    /// <summary>
    /// Program's entry point in this process, with the console captured. Only in <see cref="ConsoleCollection"/>; the
    /// default output format follows this process's real stdout, so tests pass --json or --text.
    /// </summary>
    public static async Task<Run> RunAsync(params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var (previousOut, previousError) = (Console.Out, Console.Error);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = EntryPoint.Invoke(null, [args]) switch
            {
                Task<int> task => await task,
                int code => code,
                var other => throw new InvalidOperationException($"Unexpected entry point result {other}."),
            };
            return new Run(exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>A command built like opticli's own and invoked in this process, with the console captured.</summary>
    public static async Task<Run> RunCommandAsync(Action<Command, GlobalOptions> build, string[] args, CancellationToken cancellationToken = default)
    {
        var options = new GlobalOptions();
        var root = new RootCommand("test");
        options.AddTo(root);
        var command = new Command("probe");
        build(command, options);
        root.Subcommands.Add(command);

        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var (previousOut, previousError) = (Console.Out, Console.Error);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var parse = root.Parse(["probe", .. args]);
            Assert.Empty(parse.Errors);
            var exit = await parse.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false }, cancellationToken);
            return new Run(exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>The built opticli.dll as a child process, so stdout is redirected as it is for an agent.</summary>
    public static async Task<Run> RunProcessAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Parse errors and help need no project; a neutral directory keeps the repository's own files out of it.
            WorkingDirectory = Path.GetTempPath(),
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        foreach (var name in new[] { "OPTICLI_DB", "DOTNET_STARTUP_HOOKS" })
        {
            start.Environment.Remove(name);
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return new Run(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>The repository root (where opticli.slnx is), for the README and the skill files.</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "opticli.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("opticli.slnx not found above the test output.");
    }
}
