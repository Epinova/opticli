using System.Globalization;

namespace OptiCli.Core.Serve;

/// <summary>
/// Claims for ports a starting <c>serve</c> has picked but its site doesn't listen on yet, so two <c>serve</c>s started at
/// the same moment (for different projects, or with separate state) don't pick the same free port and one of the sites
/// then fail to bind.
/// </summary>
/// <remarks>
/// A claim is an empty file per port, held open with an exclusive lock (<see cref="FileShare.None"/>, an advisory
/// <c>flock</c> on Unix) by the claiming process: it counts exactly as long as that process holds it, and goes when the
/// process releases it or exits (a background <c>serve</c> exits once its site listens, a foreground one when the site
/// stops), so there are no stale claims to judge or delete. The files are never written to or deleted. They live in a
/// folder only this user can use: <c>$XDG_RUNTIME_DIR/opticli-ports</c>, else <c>~/.local/state/opticli/ports</c> on Unix
/// (not the shared <c>/tmp</c>, and not under <c>XDG_STATE_HOME</c>, which separate runs may set differently), the user's
/// temp folder on Windows. On Unix it is made (and kept) 0700, which only its owner can do; a folder or claim file that is a
/// symbolic link, or a folder that isn't the user's, is not used. Best effort: when the folder can't be used, every port
/// counts as unclaimed, as before claims existed.
/// </remarks>
public sealed class PortClaims(string directory)
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Dictionary<int, FileStream> _held = [];

    public static PortClaims ForUser { get; } = new(UserDirectory());

    /// <summary>The claims folder for this user (see the remarks).</summary>
    public static string UserDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Path.GetTempPath(), "opticli-ports");
        }
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime) && Path.IsPathRooted(runtime) && Directory.Exists(runtime))
        {
            return Path.Combine(runtime, "opticli-ports");
        }
        var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } set ? set : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "opticli", "ports");
    }

    /// <param name="isFree">Whether nothing listens on the port (<see cref="PortSelector.IsFree"/>).</param>
    /// <returns>A probe for <see cref="PortSelector.Select"/>: free, and now claimed by this process.</returns>
    public Func<int, bool> FreeAndClaimed(Func<int, bool> isFree) => port => isFree(port) && TryClaim(port);

    /// <summary>Claims <paramref name="port"/> for this process, unless another process holds a claim on it.</summary>
    /// <returns>False only when another process holds it; true when claimed, or when claims can't be used here.</returns>
    public bool TryClaim(int port)
    {
        lock (_held)
        {
            if (_held.ContainsKey(port) || !UsableFolder())
            {
                return true;
            }
            var file = Path.Combine(directory, port.ToString(CultureInfo.InvariantCulture));
            try
            {
                if (new FileInfo(file).LinkTarget is not null)
                {
                    return true;
                }
                // OpenOrCreate without truncating: an existing file's content is never touched.
                _held[port] = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) when (File.Exists(file))
            {
                // Locked: another process holds the claim.
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    /// <summary>Lets go of every claim this process holds (its site listens on the port now, or didn't start).</summary>
    public void ReleaseAll()
    {
        lock (_held)
        {
            foreach (var stream in _held.Values)
            {
                stream.Dispose();
            }
            _held.Clear();
        }
    }

    /// <summary>The folder exists or was made, isn't a symbolic link, and (on Unix) is this user's own, mode 0700.</summary>
    private bool UsableFolder()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, Private);
            }
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
            if (!OperatingSystem.IsWindows())
            {
                // Only the owner (or root) may change a folder's mode: this proves it is ours, and keeps it private.
                File.SetUnixFileMode(directory, Private);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
