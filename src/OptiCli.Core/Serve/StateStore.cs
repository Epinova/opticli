using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;
using OptiCli.Protocol;

namespace OptiCli.Core.Serve;

/// <summary>
/// The <see cref="ServeState"/> file and log files of one project, named by a hash of the project
/// directory so any number of sites can be served side by side.
/// </summary>
public sealed class StateStore
{
    /// <summary>Logs kept per project: the latest run's and the ones before it.</summary>
    public const int LogsKept = 3;

    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode UserOnlyDirectory = UserOnlyFile | UnixFileMode.UserExecute;

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _key;

    public StateStore(string stateDirectory, string projectDirectory)
    {
        Directory = stateDirectory;
        ProjectDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        _key = Key(ProjectDirectory);
        StatePath = Path.Combine(stateDirectory, $"{_key}.json");
        LogPath = Path.Combine(stateDirectory, $"{_key}.log");
        LockPath = Path.Combine(stateDirectory, $"{_key}.lock");
        DriftPath = Path.Combine(stateDirectory, $"{_key}.drift.json");
    }

    public string Directory { get; }

    public string ProjectDirectory { get; }

    public string StatePath { get; }

    /// <summary>The latest run's log; earlier runs' are <see cref="PreviousLogPath"/>.</summary>
    public string LogPath { get; }

    /// <summary>Held from "is a site running?" until the state file is written, so two starts can't both decide no.</summary>
    public string LockPath { get; }

    /// <summary>What <c>serve</c> compared before starting a site against a shared database, for the site agent to read.</summary>
    public string DriftPath { get; }

    /// <summary>The log of the run <paramref name="runsAgo"/> runs before the latest (1 up to <see cref="LogsKept"/> - 1).</summary>
    public string PreviousLogPath(int runsAgo) => Path.Combine(Directory, $"{_key}.{runsAgo}.log");

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
    /// <exception cref="CorruptStateException">The state file exists but is unreadable.</exception>
    public ServeState? Read()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }
        ServeState? state;
        try
        {
            state = JsonSerializer.Deserialize<ServeState>(File.ReadAllText(StatePath), JsonOutput.Options);
        }
        catch (JsonException ex)
        {
            throw new CorruptStateException(StatePath, ex.Message);
        }
        // "null", or an object without the fields every state has (the deserializer leaves missing ones null).
        return state is { ProjectDirectory: not null, Token: not null, LogPath: not null }
            ? state
            : throw new CorruptStateException(StatePath, "it holds no serve state.");
    }

    /// <summary>
    /// Takes the project's start lock, waiting while another <c>serve</c> or <c>env</c> holds it. The operating system
    /// releases it when the holding process exits, however it exits.
    /// </summary>
    /// <exception cref="UsageException">Another opticli still held it after <paramref name="timeout"/>.</exception>
    public async Task<IDisposable> LockAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        EnsureDirectory();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // FileShare.None is an exclusive lock: a share-mode lock on Windows, flock on Unix.
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UserOnlyFile;
                }
                return new FileStream(LockPath, options);
            }
            catch (IOException) when (waited.Elapsed < timeout)
            {
                await Task.Delay(LockPollInterval, cancellationToken);
            }
            catch (IOException)
            {
                throw new UsageException(
                    $"Another opticli serve or env for this project is still starting the site (it held {LockPath} for {timeout.TotalSeconds:0} s).",
                    "Wait for it to finish, then check `opticli serve --status`.");
            }
        }
    }

    /// <summary>Writes atomically (temp file + rename) with user-only permissions from the start.</summary>
    public void Write(ServeState state) => WriteUserOnly(StatePath, JsonSerializer.Serialize(state, JsonOutput.Options));

    /// <summary>Writes <see cref="DriftPath"/>, in the agent's JSON, for the site agent of the run about to start.</summary>
    /// <returns>Its path.</returns>
    public string WriteStartupDrift(StartupDrift drift)
    {
        WriteUserOnly(DriftPath, JsonSerializer.Serialize(drift, AgentJson.Options));
        return DriftPath;
    }

    private void WriteUserOnly(string path, string text)
    {
        EnsureDirectory();
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = OpenUserOnly(temp, FileMode.CreateNew))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Removes the state file, and what <c>serve</c> compared before the start (<see cref="DriftPath"/>) with it.</summary>
    public void Delete()
    {
        if (File.Exists(StatePath))
        {
            File.Delete(StatePath);
        }
        if (File.Exists(DriftPath))
        {
            File.Delete(DriftPath);
        }
    }

    /// <summary>
    /// Deletes the state file only while it is the one with <paramref name="token"/>: a run that failed must not remove
    /// the state of a site another run registered meanwhile.
    /// </summary>
    public void DeleteIfOwned(string token)
    {
        try
        {
            if (Read() is { } state && state.Token == token)
            {
                Delete();
            }
        }
        catch (CorruptStateException)
        {
            // This run wrote a readable one, so a corrupt one isn't it.
        }
    }

    /// <summary>
    /// Starts a new log for a run and returns a stream the site's output is copied to. The previous logs move to
    /// <see cref="PreviousLogPath"/>; only the last <see cref="LogsKept"/> are kept.
    /// </summary>
    public FileStream CreateLog()
    {
        EnsureDirectory();
        RotateLogs();
        return OpenUserOnly(LogPath, FileMode.Create);
    }

    /// <summary>Starts a new, empty, user-only log, for a process that opens it itself.</summary>
    public void PrepareLog()
    {
        using var _ = CreateLog();
    }

    private void RotateLogs()
    {
        try
        {
            for (var runsAgo = LogsKept - 1; runsAgo >= 1; runsAgo--)
            {
                var from = runsAgo == 1 ? LogPath : PreviousLogPath(runsAgo - 1);
                if (!File.Exists(from))
                {
                    continue;
                }
                File.Move(from, PreviousLogPath(runsAgo), overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    // A log from before opticli made logs user-only.
                    File.SetUnixFileMode(PreviousLogPath(runsAgo), UserOnlyFile);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log another process still has open (Windows): the new run overwrites the latest one instead.
        }
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

/// <summary>
/// The state file can't be read. <c>serve --status</c>, <c>--stop</c> and a new <c>serve</c> treat it as stale and
/// remove it; <c>doctor</c> reports it.
/// </summary>
public sealed class CorruptStateException(string path, string reason)
    : OptiCliException(ErrorCode.Usage, $"The opticli state file {path} is corrupt: {reason}", "Run `opticli serve --stop` to remove it, then start again.")
{
    public string StatePath { get; } = path;
}
