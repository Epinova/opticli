using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OptiCli.Core.Discovery;

namespace OptiCli.Core.Seals;

/// <summary>
/// The site build a sealed apply runs against production, as a hash: every file in its output folder, the project's
/// <c>appsettings*.json</c> (the site's content root) and its user secrets. Code changed after the seal would run with
/// production's write login, so the run refuses a build that differs. The git commit is only shown, to the user.
/// </summary>
/// <param name="Output">The site DLL the run starts.</param>
/// <param name="Built">When the DLL was written (UTC).</param>
/// <param name="Hash">SHA-256 (hex) of the files, each with its path.</param>
/// <param name="Commit">The project's git commit (12 characters); null outside a git repository or without git.</param>
/// <param name="Uncommitted">Tracked files had changes that weren't committed; null when not known.</param>
public sealed record BuildFingerprint(string Output, DateTime Built, string Hash, int Files, string? Commit, bool? Uncommitted)
{
    public static BuildFingerprint Of(ProjectInfo project, string outputDll, OptiCliEnvironment environment)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputDll))!;
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => (Name: "output/" + Path.GetRelativePath(folder, f).Replace('\\', '/'), Path: f))
            .Concat(Directory.EnumerateFiles(project.Directory, "appsettings*.json").Select(f => (Name: "project/" + Path.GetFileName(f), Path: f)));
        if (project.Project.UserSecretsId is { } id && !id.Contains("$(", StringComparison.Ordinal) && environment.UserSecretsFile(id) is var secrets && File.Exists(secrets))
        {
            files = files.Append(("secrets.json", secrets));
        }
        var list = files.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("opticli build v1\n"u8);
        foreach (var (name, path) in list)
        {
            var bytes = File.ReadAllBytes(path);
            hash.AppendData(Encoding.UTF8.GetBytes($"{name}\n{bytes.Length.ToString(CultureInfo.InvariantCulture)}\n"));
            hash.AppendData(bytes);
        }
        var (commit, uncommitted) = GitState(project.Directory);
        return new BuildFingerprint(Path.GetFullPath(outputDll), File.GetLastWriteTimeUtc(outputDll), Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), list.Count, commit, uncommitted);
    }

    /// <summary>The commit and whether tracked files changed; nulls when git isn't there or this isn't a repository.</summary>
    private static (string? Commit, bool? Uncommitted) GitState(string directory)
    {
        var head = Git.Output(directory, "rev-parse", "--short=12", "HEAD");
        if (head is null)
        {
            return (null, null);
        }
        var status = Git.Output(directory, "status", "--porcelain", "--untracked-files=no");
        return (head.Trim(), status is null ? null : status.Trim().Length > 0);
    }
}
