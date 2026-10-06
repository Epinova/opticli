using OptiCli.Core.Errors;

namespace OptiCli.Core.Serve;

/// <summary>Finds the agent DLL that <c>serve</c> injects with <c>DOTNET_STARTUP_HOOKS</c>: one build per CMS major.</summary>
public static class AgentLocator
{
    public const string FileName = "OptiCli.Agent.dll";

    /// <summary>
    /// <c>&lt;cli dir&gt;/agent/cms&lt;major&gt;/OptiCli.Agent.dll</c> (the tool package layout, and the CLI's own build
    /// output, which copies both builds there), else the agent project's build output for that major in this repository
    /// (running the CLI from a checkout whose agent was built separately).
    /// </summary>
    /// <param name="cmsMajor">12 or 13 (<see cref="AgentSelection.SupportedMajors"/>).</param>
    /// <exception cref="NotFoundException">Neither exists.</exception>
    public static string Locate(string cliDirectory, int cmsMajor)
    {
        return Candidates(cliDirectory, cmsMajor).FirstOrDefault(File.Exists)
            ?? throw new NotFoundException(
                $"The opticli agent for CMS {cmsMajor} ({FileName}) was not found next to the CLI ({Path.Combine(cliDirectory, "agent", Folder(cmsMajor))}).",
                "Reinstall the tool (dotnet tool update -g OptiCli), or in a checkout build src/OptiCli.Agent.");
    }

    public static IEnumerable<string> Candidates(string cliDirectory, int cmsMajor)
    {
        yield return Path.Combine(cliDirectory, "agent", Folder(cmsMajor), FileName);

        // Checkout layout: <repo>/src/OptiCli/bin/<cfg>/<tfm>/ next to <repo>/src/OptiCli.Agent/.
        for (var directory = new DirectoryInfo(cliDirectory); directory is not null; directory = directory.Parent)
        {
            var agentProject = Path.Combine(directory.FullName, "src", "OptiCli.Agent");
            if (Directory.Exists(agentProject))
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    yield return Path.Combine(agentProject, "bin", configuration, TargetFramework(cmsMajor), FileName);
                }
                yield break;
            }
        }
    }

    /// <summary>The package folder of the build for <paramref name="cmsMajor"/>: <c>cms12</c>, <c>cms13</c>.</summary>
    public static string Folder(int cmsMajor) => $"cms{cmsMajor}";

    /// <summary>The agent project's target framework for <paramref name="cmsMajor"/> (CMS 13 needs .NET 10).</summary>
    public static string TargetFramework(int cmsMajor) => cmsMajor >= 13 ? "net10.0" : "net8.0";
}
