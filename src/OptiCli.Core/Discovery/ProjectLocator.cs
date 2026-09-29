using OptiCli.Core.Errors;
using OptiCli.Core.SourceScan;

namespace OptiCli.Core.Discovery;

/// <summary>
/// Finds the CMS web project for the working directory. Never guesses: several CMS projects are
/// an error that asks for <c>--project</c>.
/// </summary>
public static class ProjectLocator
{
    private static readonly string[] SolutionPatterns = ["*.sln", "*.slnx"];

    /// <summary>How deep to look below the working directory when nothing is found above it.</summary>
    private const int DownwardSearchDepth = 4;

    public static ProjectInfo Locate(string? explicitPath, string currentDirectory)
    {
        return explicitPath is null
            ? LocateFrom(Path.GetFullPath(currentDirectory))
            : LocateExplicit(Path.GetFullPath(explicitPath, currentDirectory));
    }

    private static ProjectInfo LocateExplicit(string path)
    {
        if (File.Exists(path))
        {
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = CsprojFile.TryLoad(path)
                    ?? throw new UsageException($"'{path}' is not a readable project file.");
                return Create(project, FindSolutionAbove(Path.GetDirectoryName(path)!), "--project");
            }
            if (IsSolutionFile(path))
            {
                return PickUnder(Path.GetDirectoryName(path)!, path, $"--project solution {path}");
            }
            throw new UsageException($"'{path}' is not a .csproj, .sln or .slnx file.", "Pass the site's .csproj or the directory that contains it.");
        }

        if (!Directory.Exists(path))
        {
            throw new NotFoundException($"--project path '{path}' does not exist.");
        }

        var local = LoadProjects(Directory.GetFiles(path, "*.csproj"));
        if (local.Count == 1)
        {
            return Create(local[0], FindSolutionAbove(path), "--project");
        }
        return PickUnder(path, FindSolutionAbove(path), $"--project directory {path}");
    }

    private static ProjectInfo LocateFrom(string start)
    {
        for (var directory = start; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var projects = LoadProjects(Directory.GetFiles(directory, "*.csproj"));
            var cms = projects.Where(p => p.IsCmsProject).ToList();
            if (cms.Count == 1)
            {
                return Create(cms[0], FindSolutionAbove(directory), $"nearest CMS project above the working directory ({directory})");
            }

            if (SolutionIn(directory) is { } solution)
            {
                return PickUnder(directory, solution, $"only CMS project under {solution}");
            }
        }

        return PickUnder(start, null, $"only CMS project below the working directory ({start})", DownwardSearchDepth);
    }

    /// <summary>Searches <paramref name="root"/> for CMS projects; exactly one must remain.</summary>
    private static ProjectInfo PickUnder(string root, string? solution, string howFound, int maxDepth = int.MaxValue)
    {
        var cms = LoadProjects(SourceTree.EnumerateFiles(root, "*.csproj", maxDepth)).Where(p => p.IsCmsProject).ToList();

        // A test project may reference the CMS package too; the web SDK marks the actual site.
        if (cms.Count > 1 && cms.Count(p => p.IsWebProject) == 1)
        {
            cms = cms.Where(p => p.IsWebProject).ToList();
        }

        return cms.Count switch
        {
            1 => Create(cms[0], solution, howFound),
            0 => throw new NotFoundException(
                $"No Optimizely CMS 12 project (a .csproj referencing {CsprojFile.CmsPackage}) found under {root}.",
                "Run opticli from the site's repository, or pass --project <path to the site's .csproj>."),
            _ => throw new UsageException(
                $"Found {cms.Count} Optimizely CMS projects under {root}: {string.Join(", ", cms.Select(p => Path.GetRelativePath(root, p.Path)))}.",
                $"Pass --project to choose one, e.g. --project {Path.GetRelativePath(root, cms[0].Path)}"),
        };
    }

    private static ProjectInfo Create(CsprojFile project, string? solution, string howFound)
    {
        var directory = Path.GetDirectoryName(project.Path)!;
        var sourceRoot = solution is null ? directory : Path.GetDirectoryName(solution)!;
        return new ProjectInfo(project, directory, solution, sourceRoot, howFound);
    }

    private static string? FindSolutionAbove(string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (SolutionIn(current) is { } solution)
            {
                return solution;
            }
        }
        return null;
    }

    private static string? SolutionIn(string directory) =>
        SolutionPatterns.SelectMany(p => Directory.GetFiles(directory, p)).Order(StringComparer.Ordinal).FirstOrDefault();

    private static List<CsprojFile> LoadProjects(IEnumerable<string> paths) =>
        paths.Select(CsprojFile.TryLoad).OfType<CsprojFile>().ToList();

    private static bool IsSolutionFile(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
}
