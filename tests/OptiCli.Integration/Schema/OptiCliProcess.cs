using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace OptiCli.Integration.Schema;

/// <summary>One run of the opticli command line: what it printed, and the envelope parsed from stdout.</summary>
internal sealed record CliRun(IReadOnlyList<string> Args, int ExitCode, string Stdout, string Stderr)
{
    /// <summary>The JSON envelope (<c>{ok, data, meta}</c> or <c>{ok: false, error}</c>); null when stdout isn't JSON.</summary>
    public JsonObject? Envelope { get; } = Parse(Stdout);

    public bool Ok => Envelope?["ok"]?.GetValue<bool>() == true;

    public JsonNode? Data => Envelope?["data"];

    /// <summary>The command line as a shell would show it, with a connection string's value left out.</summary>
    public string CommandLine => string.Join(' ', Args.Select((a, i) =>
        i > 0 && Args[i - 1] == "--connection" ? "<connection string>"
        : a.Contains(' ', StringComparison.Ordinal) || a.Length == 0 ? $"'{a}'"
        : a));

    private static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Runs this checkout's opticli as a process (<c>dotnet src/OptiCli/bin/&lt;configuration&gt;/net8.0/opticli.dll</c>, the
/// build this test project's own configuration made, or <c>OPTICLI_IT_CLI</c>), so a test sees exactly what a user's
/// shell or the npm package sees.
/// </summary>
internal static class OptiCliProcess
{
    public const string CliVariable = "OPTICLI_IT_CLI";

    /// <summary>The program and the arguments before opticli's own: <c>dotnet path/opticli.dll</c>, or an executable.</summary>
    public static (string Program, string[] Prefix) Command { get; } = Locate();

    public static async Task<CliRun> RunAsync(IReadOnlyList<string> args, string? input = null, IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(Command.Program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in Command.Prefix.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            start.Environment[name] = value;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {Command.Program}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
        }
        process.StandardInput.Close();
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"opticli {string.Join(' ', args.TakeWhile(a => a != "--connection"))} didn't exit within {(timeout ?? TimeSpan.FromMinutes(5)).TotalSeconds} s.");
        }
        return new CliRun(args, process.ExitCode, await stdout, await stderr);
    }

    private static (string, string[]) Locate()
    {
        if (Environment.GetEnvironmentVariable(CliVariable) is { Length: > 0 } configured)
        {
            return configured.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? ("dotnet", [Path.GetFullPath(configured)]) : (configured, []);
        }
        // tests/OptiCli.Integration/bin/<configuration>/net8.0/ → src/OptiCli/bin/<configuration>/net8.0/opticli.dll
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = here.Parent!.Name;
        var root = here.Parent.Parent!.Parent!.Parent!.Parent!.FullName;
        var dll = Path.Combine(root, "src", "OptiCli", "bin", configuration, "net8.0", "opticli.dll");
        if (!File.Exists(dll))
        {
            throw new InvalidOperationException($"No opticli build at {dll}: build src/OptiCli ({configuration}), or set {CliVariable}.");
        }
        return ("dotnet", [dll]);
    }
}
