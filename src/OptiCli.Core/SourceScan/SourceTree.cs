namespace OptiCli.Core.SourceScan;

/// <summary>
/// Walks a source tree for project, C# and Razor files while skipping build output, packages and
/// dot-directories (.git, .vs, .claude worktrees, ...), which would otherwise produce duplicates.
/// </summary>
public static class SourceTree
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages",
    };

    public static IEnumerable<string> EnumerateFiles(string root, string searchPattern, int maxDepth = int.MaxValue)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();

            string[] files, subdirectories;
            try
            {
                files = Directory.GetFiles(directory, searchPattern);
                subdirectories = depth < maxDepth ? Directory.GetDirectories(directory) : [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var subdirectory in subdirectories.OrderByDescending(d => d, StringComparer.Ordinal))
            {
                if (!IsSkipped(Path.GetFileName(subdirectory)) && !IsLink(subdirectory))
                {
                    pending.Push((subdirectory, depth + 1));
                }
            }
        }
    }

    public static bool IsSkipped(string directoryName) =>
        directoryName.StartsWith('.') || SkippedDirectories.Contains(directoryName);

    /// <summary>Symlinked directories are not followed: they can loop and usually duplicate real sources.</summary>
    private static bool IsLink(string directory)
    {
        try
        {
            return File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }
}
