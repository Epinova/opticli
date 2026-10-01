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

    /// <summary>Folders below <c>&lt;tfm&gt;/</c> that hold something other than a RuntimeIdentifier build.</summary>
    private static readonly string[] NotRuntimeDirectories = ["publish", "runtimes", "refs", "wwwroot"];

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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

    /// <summary>
    /// Where MSBuild puts <c>&lt;AssemblyName&gt;.dll</c> in Debug and Release, as the project sets it up:
    /// <list type="bullet">
    /// <item><c>bin/&lt;cfg&gt;/&lt;tfm&gt;/</c> for every target framework, or the <c>OutputPath</c> or <c>BaseOutputPath</c>
    /// the project sets, with a <c>&lt;rid&gt;/</c> folder below it when it builds for a RuntimeIdentifier;</item>
    /// <item><c>artifacts/bin/&lt;project&gt;/&lt;cfg&gt;[_&lt;tfm&gt;][_&lt;rid&gt;]/</c> with <c>UseArtifactsOutput</c>.</item>
    /// </list>
    /// The most likely path comes first.
    /// </summary>
    public static IEnumerable<string> Candidates(ProjectInfo project)
    {
        var name = AssemblyName(project.Project);
        var seen = new HashSet<string>(PathComparer);
        foreach (var configuration in Configurations)
        {
            foreach (var directory in OutputDirectories(project, configuration))
            {
                var dll = Path.Combine(directory, $"{name}.dll");
                if (seen.Add(dll))
                {
                    yield return dll;
                }
            }
        }
    }

    private static IEnumerable<string> OutputDirectories(ProjectInfo project, string configuration)
    {
        var csproj = project.Project;
        if (string.Equals(csproj.UseArtifactsOutput, "true", StringComparison.OrdinalIgnoreCase))
        {
            var artifacts = ExpandPath(csproj.ArtifactsPath, configuration, project.Directory) ?? Path.Combine(ArtifactsBase(project), "artifacts");
            var root = Path.Combine(artifacts, "bin", Path.GetFileNameWithoutExtension(csproj.Path));
            // The configuration in lower case, then _<tfm> when multi-targeting and _<rid> with a RuntimeIdentifier.
            var pivot = configuration.ToLowerInvariant();
            return Subdirectories(root)
                .Where(d => Path.GetFileName(d).StartsWith(pivot + "_", StringComparison.OrdinalIgnoreCase))
                .Prepend(Path.Combine(root, pivot));
        }

        var output = ExpandPath(csproj.OutputPath, configuration, project.Directory)
            ?? Path.Combine(ExpandPath(csproj.BaseOutputPath, configuration, project.Directory) ?? Path.Combine(project.Directory, "bin"), configuration);
        var frameworks = (csproj.TargetFramework ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => !f.Contains("$(", StringComparison.Ordinal))
            .ToList();
        // Framework set where opticli can't read it: take whatever was built.
        var tfmDirectories = frameworks.Count > 0
            ? frameworks.Select(tfm => Path.Combine(output, tfm.ToLowerInvariant())).ToList()
            : Subdirectories(output).ToList();
        return
        [
            .. tfmDirectories,
            .. tfmDirectories.SelectMany(Subdirectories).Where(d => !NotRuntimeDirectories.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)),
            // AppendTargetFrameworkToOutputPath=false.
            output,
        ];
    }

    /// <summary>Where the default <c>artifacts</c> folder goes: next to the <c>Directory.Build.props</c>, else in the project.</summary>
    private static string ArtifactsBase(ProjectInfo project) =>
        project.Project.Imports.FirstOrDefault(f => Path.GetFileName(f).Equals(MsBuildProperties.DirectoryBuildProps, StringComparison.OrdinalIgnoreCase)) is { } props
            ? Path.GetDirectoryName(props)!
            : project.Directory;

    /// <summary>An MSBuild path property for one configuration; null when unset or still an expression opticli can't expand.</summary>
    private static string? ExpandPath(string? value, string configuration, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var expanded = value.Replace("$(Configuration)", configuration, StringComparison.OrdinalIgnoreCase);
        return expanded.Contains("$(", StringComparison.Ordinal)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded.Replace('\\', Path.DirectorySeparatorChar), projectDirectory));
    }

    private static IEnumerable<string> Subdirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetDirectories(directory) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary><c>&lt;AssemblyName&gt;</c> unless it is an MSBuild expression, else the csproj file name.</summary>
    public static string AssemblyName(CsprojFile project) =>
        project.AssemblyName is { } name && !name.Contains("$(", StringComparison.Ordinal)
            ? name
            : Path.GetFileNameWithoutExtension(project.Path);

    /// <summary>
    /// The newest source file of the project, or of a project it references (directly or not), that was changed after
    /// <paramref name="dll"/> was built, if any.
    /// </summary>
    public static string? NewerSource(ProjectInfo project, string dll)
    {
        var built = File.GetLastWriteTimeUtc(dll);
        return SourceDirectories(project.Project)
            .SelectMany(SourceFiles)
            .Select(file => (File: file, Time: File.GetLastWriteTimeUtc(file)))
            .Where(f => f.Time > built)
            .OrderByDescending(f => f.Time)
            .Select(f => f.File)
            .FirstOrDefault();
    }

    /// <summary>The directories of the project and of every project it references; one inside another is scanned with it.</summary>
    internal static IReadOnlyList<string> SourceDirectories(CsprojFile project)
    {
        var projects = new HashSet<string>(PathComparer) { project.Path };
        var pending = new Queue<string>(project.ProjectReferences);
        while (pending.Count > 0)
        {
            var reference = pending.Dequeue();
            if (!projects.Add(reference))
            {
                continue;
            }
            // Only its references are needed, which don't depend on Directory.Build.props.
            foreach (var next in CsprojFile.TryLoad(reference, evaluateBuildProps: false)?.ProjectReferences ?? [])
            {
                pending.Enqueue(next);
            }
        }

        var directories = projects
            .Select(p => Path.GetDirectoryName(p)!)
            .Where(Directory.Exists)
            .Distinct(PathComparer)
            .OrderBy(d => d.Length)
            .ToList();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return directories
            .Where((directory, i) => !directories.Take(i).Any(outer => directory.StartsWith(outer + Path.DirectorySeparatorChar, comparison)))
            .ToList();
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
