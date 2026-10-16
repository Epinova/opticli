namespace OptiCli.Core.Tests;

/// <summary>A throwaway directory tree under the system temp path, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opticli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes <paramref name="content"/> to a path relative to the root, creating directories.</summary>
    public string Write(string relativePath, string content, bool withBom = false)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(withBom));
        return full;
    }

    public string Combine(string relativePath) => System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // Windows won't delete a read-only file, and git's objects are read-only: clear the flag and try again.
            try
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover temp directory is harmless.
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory is harmless.
        }
    }
}
