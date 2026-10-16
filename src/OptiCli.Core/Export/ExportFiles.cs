using OptiCli.Core.Queries;

namespace OptiCli.Core.Export;

/// <summary>The names of the files an export (or a sync) writes for media.</summary>
public static class ExportFiles
{
    /// <summary>
    /// The file's name in the plan: the media item's name when it has the stored file's extension, else the stored
    /// file's name; characters a file name can't have on some system become <c>_</c>.
    /// </summary>
    public static string FileName(string? name, string blobUri)
    {
        var stored = BlobLocator.ParseUri(blobUri).File ?? "file";
        var chosen = name is { Length: > 0 } && Path.GetExtension(name).Equals(Path.GetExtension(stored), StringComparison.OrdinalIgnoreCase) ? name : stored;
        return Safe(chosen);
    }

    /// <summary>A name every file system takes: characters some can't have become <c>_</c>; never empty.</summary>
    public static string Safe(string name)
    {
        var text = new string(name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return text.Length == 0 ? "file" : text;
    }
}
