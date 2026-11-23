using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>The rules for files opticli uploads: regular files only, within the size cap, and inside the plan's folder.</summary>
public static class MediaFiles
{
    /// <summary>The file to read: <paramref name="path"/>, or what a symlink there finally points at.</summary>
    /// <exception cref="UsageException">Missing, not a regular file, empty or too large.</exception>
    public static FileInfo Check(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
        {
            try
            {
                file = file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo
                    ?? throw new UsageException($"{path} is a link to something that isn't a file.");
            }
            catch (IOException ex)
            {
                // A loop of links, say: the user's to fix, not a crash.
                throw new UsageException($"{path} can't be followed: {ex.Message}");
            }
        }
        if (!file.Exists)
        {
            throw new UsageException(Directory.Exists(file.FullName) ? $"{file.FullName} is a directory, not a file." : $"File {file.FullName} does not exist.");
        }
        if ((file.Attributes & (FileAttributes.Device | FileAttributes.Directory)) != 0)
        {
            throw new UsageException($"{file.FullName} is not a regular file.");
        }
        return file.Length switch
        {
            0 => throw new UsageException($"{file.FullName} is empty."),
            > UploadRequest.MaxBytes => throw new UsageException(
                $"{file.FullName} is {file.Length / (1024 * 1024)} MB; opticli uploads at most {UploadRequest.MaxBytes / (1024 * 1024)} MB.",
                "Upload larger files in the CMS edit UI."),
            _ => file,
        };
    }

    /// <summary>The file's SHA-256, hex, as the site agent compares it with a stored file.</summary>
    public static async Task<string> Sha256Async(FileInfo file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenRead();
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>
    /// A plan's <c>file</c>, relative to the plan's folder. It must stay inside that folder, also after following
    /// symlinks, unless <paramref name="allowOutside"/>: a checked-in plan shouldn't be able to upload arbitrary files.
    /// </summary>
    /// <exception cref="UsageException">Absolute, or escapes the folder.</exception>
    public static string ForPlan(string file, string planDirectory, bool allowOutside)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(planDirectory));
        var full = Path.GetFullPath(file, root);
        if (allowOutside)
        {
            return full;
        }
        if (Path.IsPathRooted(file))
        {
            throw Outside(file, "is an absolute path");
        }
        if (!Inside(full, root))
        {
            throw Outside(file, $"is outside the plan's folder {root}");
        }
        var realRoot = RealPath(root);
        if (RealPath(full) is var real && !Inside(real, realRoot))
        {
            throw Outside(file, $"links to {real}, outside the plan's folder {realRoot}");
        }
        return full;
    }

    /// <summary>
    /// <paramref name="path"/> as the OS resolves it: every symlink on it followed, the file's and its directories' alike.
    /// A link's target is walked part by part, so <c>..</c> in it climbs from where the links before it led, as the
    /// kernel does (<c>f -> s/../secret</c> with <c>s -> /elsewhere/dir</c> is <c>/elsewhere/secret</c>), never from the
    /// text (<c>Path.GetFullPath</c> would say <c>./secret</c>).
    /// </summary>
    /// <exception cref="UsageException">A loop of links.</exception>
    private static string RealPath(string path, int depth = 0)
    {
        if (depth > MaxLinks)
        {
            throw new UsageException($"{path} goes through more than {MaxLinks} symbolic links (a loop?).");
        }
        var current = Path.GetPathRoot(path) ?? "";
        foreach (var part in path[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)) ?? current;
                continue;
            }
            var next = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            current = entry.LinkTarget is { } target
                ? RealPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target), depth + 1)
                : next;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>How many links a path may go through, as Linux's <c>MAXSYMLINKS</c>.</summary>
    private const int MaxLinks = 40;

    private static bool Inside(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static UsageException Outside(string file, string why) => new(
        $"file '{file}' {why}.",
        "Plan files are relative to the plan and stay inside its folder; pass --allow-outside to apply if this is intended.");
}
