using System.Security.Cryptography;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Users;

/// <summary>
/// The password of a user <c>opticli users add</c> made without being given one: generated, and written to a file only
/// the user can read, under opticli's state directory, one folder per project. The password itself is never printed
/// (the safety model's "passwords are never printed"); the output names the file.
/// </summary>
public static class PasswordFiles
{
    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode UserOnlyDirectory = UserOnlyFile | UnixFileMode.UserExecute;

    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "!#%+-=?@_";

    /// <summary>
    /// 24 characters with lower and upper case letters, digits and symbols, so it meets ASP.NET Identity's default rules
    /// and the CMS's (any one of each, at least 6), and the stricter ones sites set. Easily confused characters are left out.
    /// </summary>
    public static string Generate(int length = 24)
    {
        var sets = new[] { Lower, Upper, Digits, Symbols };
        var all = string.Concat(sets);
        var chars = sets.Select(set => set[RandomNumberGenerator.GetInt32(set.Length)])
            .Concat(Enumerable.Range(0, length - sets.Length).Select(_ => all[RandomNumberGenerator.GetInt32(all.Length)]))
            .ToArray();
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    /// <summary><c>&lt;state&gt;/users/&lt;project key&gt;/&lt;name&gt;.txt</c>, the project key as <c>serve</c>'s state files have it.</summary>
    public static string PathFor(string stateDirectory, string projectDirectory, string userName) =>
        Path.Combine(stateDirectory, "users", StateStore.Key(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory))), $"{FileName(userName)}.txt");

    /// <summary>The user name as a file name: anything a file name can't hold, and path separators, as '_'.</summary>
    public static string FileName(string userName)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var name = new string(userName.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).TrimStart('.');
        return name.Length == 0 ? "user" : name;
    }

    /// <summary>Writes the password (and a newline), creating the folders, readable and writable by the user only.</summary>
    public static void Write(string path, string password)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UserOnlyDirectory);
            File.SetUnixFileMode(directory, UserOnlyDirectory);
        }
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UserOnlyFile;
        }
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.WriteLine(password);
        }
        if (!OperatingSystem.IsWindows())
        {
            // An existing file keeps its mode on create; this one must be the user's only.
            File.SetUnixFileMode(path, UserOnlyFile);
        }
    }

    /// <returns>True when there was a file to delete.</returns>
    public static bool Delete(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        File.Delete(path);
        return true;
    }
}
