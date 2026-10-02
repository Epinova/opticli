namespace OptiCli.Cms.Content;

internal static class MediaFileNames
{
    /// <summary>The extension with its dot (<c>.pdf</c>), lower-case; null unless <paramref name="fileName"/> is a bare file name with one.</summary>
    public static string? Extension(string? fileName)
    {
        var name = fileName?.Trim() ?? "";
        if (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            return null;
        }
        var extension = Path.GetExtension(name);
        return extension.Length > 1 && name.Length > extension.Length ? extension.ToLowerInvariant() : null;
    }
}
