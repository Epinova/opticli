namespace OptiCli.Core.Discovery;

/// <param name="Directory">The project directory: the site's content root and where its config files live.</param>
/// <param name="SourceRoot">Where C# and Razor files are scanned: the solution directory if there is one, else <paramref name="Directory"/>.</param>
/// <param name="HowFound">Human-readable explanation of how the project was chosen, for <c>doctor</c>.</param>
public sealed record ProjectInfo(
    CsprojFile Project,
    string Directory,
    string? SolutionFile,
    string SourceRoot,
    string HowFound)
{
    public string ProjectFile => Project.Path;
}
