using OptiCli.Core.Errors;

namespace OptiCli.Core.Serve;

/// <summary>Finds the agent DLL that <c>serve</c> injects with <c>DOTNET_STARTUP_HOOKS</c>.</summary>
public static class AgentLocator
{
    public const string FileName = "OptiCli.Agent.dll";

    /// <summary>
    /// <c>&lt;cli dir&gt;/agent/OptiCli.Agent.dll</c> (the tool package layout, and the CLI's own build output,
    /// which copies the agent there), else the agent project's build output in this repository (running
    /// the CLI from a checkout whose agent was built separately).
    /// </summary>
    /// <exception cref="NotFoundException">Neither exists.</exception>
    public static string Locate(string cliDirectory)
    {
        return Candidates(cliDirectory).FirstOrDefault(File.Exists)
            ?? throw new NotFoundException(
                $"The opticli agent ({FileName}) was not found next to the CLI ({Path.Combine(cliDirectory, "agent")}).",
                "Reinstall the tool (dotnet tool update -g OptiCli), or in a checkout build src/OptiCli.Agent.");
    }

    public static IEnumerable<string> Candidates(string cliDirectory)
    {
        var packaged = Path.Combine(cliDirectory, "agent", FileName);
        yield return packaged;

        // Checkout layout: <repo>/src/OptiCli/bin/<cfg>/<tfm>/ next to <repo>/src/OptiCli.Agent/.
        for (var directory = new DirectoryInfo(cliDirectory); directory is not null; directory = directory.Parent)
        {
            var agentProject = Path.Combine(directory.FullName, "src", "OptiCli.Agent");
            if (Directory.Exists(agentProject))
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    yield return Path.Combine(agentProject, "bin", configuration, "net8.0", FileName);
                }
                yield break;
            }
        }
    }
}
