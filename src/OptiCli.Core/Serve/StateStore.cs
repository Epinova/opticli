using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;

namespace OptiCli.Core.Serve;

/// <summary>
/// The <see cref="ServeState"/> file and log file of one project, named by a hash of the project
/// directory so any number of sites can be served side by side.
/// </summary>
public sealed class StateStore
{
    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode UserOnlyDirectory = UserOnlyFile | UnixFileMode.UserExecute;

    public StateStore(string stateDirectory, string projectDirectory)
    {
        Directory = stateDirectory;
        ProjectDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        var key = Key(ProjectDirectory);
        StatePath = Path.Combine(stateDirectory, $"{key}.json");
        LogPath = Path.Combine(stateDirectory, $"{key}.log");
    }

    public string Directory { get; }

    public string ProjectDirectory { get; }

    public string StatePath { get; }

    public string LogPath { get; }

    public static StateStore For(OptiCliEnvironment environment, string projectDirectory) =>
        new(environment.StateDirectory, projectDirectory);

    /// <summary>Stable per project directory; case-insensitive on Windows like its file system.</summary>
    public static string Key(string projectDirectory)
    {
        var normalized = OperatingSystem.IsWindows() ? projectDirectory.ToUpperInvariant() : projectDirectory;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"{Sanitize(Path.GetFileName(projectDirectory))}-{Convert.ToHexString(hash, 0, 6).ToLowerInvariant()}";
    }

    /// <returns>Null when there is no state file.</returns>
    /// <exception cref="UsageException">The state file exists but is unreadable.</exception>
    public ServeState? Read()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ServeState>(File.ReadAllText(StatePath), JsonOutput.Options);
        }
        catch (JsonException ex)
        {
            throw new UsageException($"The opticli state file {StatePath} is corrupt: {ex.Message}", "Run `opticli serve --stop` to remove it, then start again.");
        }
    }

    /// <summary>Writes atomically (temp file + rename) with user-only permissions from the start.</summary>
    public void Write(ServeState state)
    {
        EnsureDirectory();
        var temp = StatePath + ".tmp";
        File.Delete(temp);
        using (var stream = OpenUserOnly(temp, FileMode.CreateNew))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(JsonSerializer.Serialize(state, JsonOutput.Options));
        }
        File.Move(temp, StatePath, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(StatePath))
        {
            File.Delete(StatePath);
        }
    }

    /// <summary>Truncates the log for a new run and returns a stream the site's output is copied to.</summary>
    public FileStream CreateLog()
    {
        EnsureDirectory();
        return OpenUserOnly(LogPath, FileMode.Create);
    }

    /// <summary>Makes an existing (or empty) log user-only, for a process that opens it itself.</summary>
    public void PrepareLog()
    {
        using var _ = CreateLog();
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        else
        {
            System.IO.Directory.CreateDirectory(Directory, UserOnlyDirectory);
        }
    }

    private static FileStream OpenUserOnly(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows())
        {
            // Applies to newly created files only; existing ones are fixed below.
            options.UnixCreateMode = UserOnlyFile;
        }
        var stream = new FileStream(path, options);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UserOnlyFile);
        }
        return stream;
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray());
        return cleaned.Length == 0 ? "project" : cleaned;
    }
}
