using OptiCli.Core.Discovery;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Serve;

/// <param name="HowFound">For status output: <c>--output</c>, user config, or which build folder.</param>
public sealed record SiteOutput(string Dll, string HowFound);

/// <summary>Finds the site's build output DLL (what <c>dotnet &lt;dll&gt;</c> runs) and whether it is out of date.</summary>
public static class OutputLocator
{
    private static readonly string[] Configurations = ["Debug", "Release"];
    private static readonly string[] SourceExtensions = [".cs", ".cshtml", ".razor", ".csproj"];
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", ".git", ".vs", ".idea"];

    /// <param name="explicitOutput"><c>--output</c>, relative to the working directory.</param>
    /// <param name="configuredOutput">The user config's <c>output</c>, relative to the project directory.</param>
    /// <exception cref="NotFoundException">No build output exists.</exception>
    public static SiteOutput Locate(ProjectInfo project, string? explicitOutput, string? configuredOutput, string currentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitOutput))
        {
            return Existing(Path.GetFullPath(explicitOutput, currentDirectory), "--output");
        }
        if (!string.IsNullOrWhiteSpace(configuredOutput))
        {
            return Existing(Path.GetFullPath(configuredOutput, project.Directory), "user config (output)");
        }

        var candidates = Candidates(project).Where(File.Exists).ToList();
        if (candidates.Count == 0)
        {
            var expected = Candidates(project).FirstOrDefault() ?? Path.Combine(project.Directory, "bin", "Debug", "<tfm>", $"{AssemblyName(project.Project)}.dll");
            throw new NotFoundException(
                $"The site has no build output (looked for {Path.GetRelativePath(project.Directory, expected)} and the Release equivalent).",
                "Build it (dotnet build), run `opticli serve --build`, or pass --output <dll>.");
        }

        // Both configurations built: the one built last is the one the developer is working with.
        var newest = candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
        return new SiteOutput(newest, $"build output ({Path.GetRelativePath(project.Directory, newest)})");
    }

    /// <summary><c>bin/{Debug,Release}/&lt;tfm&gt;/&lt;AssemblyName&gt;.dll</c> for every target framework in the csproj.</summary>
    public static IEnumerable<string> Candidates(ProjectInfo project)
    {
        var name = AssemblyName(project.Project);
        var frameworks = (project.Project.TargetFramework ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => !f.Contains("$(", StringComparison.Ordinal))
            .ToList();

        foreach (var configuration in Configurations)
        {
            var configurationDirectory = Path.Combine(project.Directory, "bin", configuration);
            // Framework set outside the csproj (Directory.Build.props): take whatever was built.
            var tfms = frameworks.Count > 0
                ? frameworks
                : System.IO.Directory.Exists(configurationDirectory)
                    ? System.IO.Directory.GetDirectories(configurationDirectory).Select(Path.GetFileName).OfType<string>().ToList()
                    : [];
            foreach (var tfm in tfms)
            {
                yield return Path.Combine(configurationDirectory, tfm, $"{name}.dll");
            }
        }
    }

    /// <summary><c>&lt;AssemblyName&gt;</c> unless it is an MSBuild expression, else the csproj file name.</summary>
    public static string AssemblyName(CsprojFile project) =>
        project.AssemblyName is { } name && !name.Contains("$(", StringComparison.Ordinal)
            ? name
            : Path.GetFileNameWithoutExtension(project.Path);

    /// <summary>The newest source file under the project directory that was changed after <paramref name="dll"/> was built, if any.</summary>
    public static string? NewerSource(string projectDirectory, string dll)
    {
        var built = File.GetLastWriteTimeUtc(dll);
        return SourceFiles(projectDirectory)
            .Select(file => (File: file, Time: File.GetLastWriteTimeUtc(file)))
            .Where(f => f.Time > built)
            .OrderByDescending(f => f.Time)
            .Select(f => f.File)
            .FirstOrDefault();
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        var pending = new Stack<string>([directory]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files, directories;
            try
            {
                files = System.IO.Directory.EnumerateFiles(current);
                directories = System.IO.Directory.EnumerateDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files.Where(f => SourceExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                yield return file;
            }
            foreach (var child in directories.Where(d => !SkippedDirectories.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
            {
                pending.Push(child);
            }
        }
    }

    private static SiteOutput Existing(string path, string howFound) =>
        File.Exists(path)
            ? new SiteOutput(path, howFound)
            : throw new NotFoundException($"The site output {path} (from {howFound}) does not exist.", "Build the site, or fix the path.");
}
