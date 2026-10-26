using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Seals;

/// <summary>
/// A plan file and every file it reads (<c>"@path"</c> values, uploads' files), read once into memory and hashed. A seal
/// records the hash; a sealed run compares it and then runs the plan from these bytes (<see cref="PlanRunner.Files"/>), so
/// nothing that could have been swapped since is read from the disk again.
/// </summary>
public sealed class PlanSnapshot
{
    private readonly IReadOnlyDictionary<string, byte[]> _files;

    private PlanSnapshot(string path, byte[] bytes, WritePlan plan, IReadOnlyDictionary<string, byte[]> files)
    {
        Path = path;
        Plan = plan;
        _files = files;
        Hash = HashOf(System.IO.Path.GetDirectoryName(path)!, bytes, files);
    }

    /// <summary>The plan file's full path.</summary>
    public string Path { get; }

    /// <summary>The folder the plan's files are relative to.</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public WritePlan Plan { get; }

    /// <summary>The files the plan reads, as full paths.</summary>
    public IReadOnlyCollection<string> Files => _files.Keys.ToList();

    /// <summary>SHA-256 (hex, lowercase) of the plan's bytes and each file's path (relative to the plan) and bytes.</summary>
    public string Hash { get; }

    /// <summary>A file's text as <see cref="File.ReadAllText(string)"/> would read it; null when the plan doesn't read that file.</summary>
    public string? Text(string fullPath) =>
        _files.TryGetValue(System.IO.Path.GetFullPath(fullPath), out var bytes) ? Decode(bytes) : null;

    /// <summary>A file's bytes as they were read; null when the plan doesn't read that file.</summary>
    public byte[]? Bytes(string fullPath) =>
        _files.TryGetValue(System.IO.Path.GetFullPath(fullPath), out var bytes) ? bytes : null;

    /// <exception cref="NotFoundException">The plan file doesn't exist.</exception>
    /// <exception cref="UsageException">The plan is malformed, or a file it reads is missing or outside its folder.</exception>
    public static async Task<PlanSnapshot> ReadAsync(string planPath, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.GetFullPath(planPath);
        if (!File.Exists(path))
        {
            throw new NotFoundException($"Plan file {path} does not exist.");
        }
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var plan = WritePlan.Parse(Decode(bytes));
        var files = new Dictionary<string, byte[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var file in PlanFiles.Files(plan.Steps, System.IO.Path.GetDirectoryName(path)!, allowOutside: false))
        {
            if (File.Exists(file))
            {
                files[file] = await File.ReadAllBytesAsync(file, cancellationToken);
            }
            else
            {
                missing.Add(file);
            }
        }
        return missing.Count == 0
            ? new PlanSnapshot(path, bytes, plan, files)
            : throw new UsageException($"The plan reads file(s) that don't exist: {string.Join(", ", missing)}.", PlanFiles.Hint) { Details = new { missing } };
    }

    internal static string HashOf(string directory, byte[] plan, IReadOnlyDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string name, byte[] bytes)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{name}\n{bytes.Length.ToString(CultureInfo.InvariantCulture)}\n"));
            hash.AppendData(bytes);
        }
        hash.AppendData("opticli plan v1\n"u8);
        Add("plan.json", plan);
        foreach (var (file, bytes) in files.Select(f => (Name: System.IO.Path.GetRelativePath(directory, f.Key).Replace('\\', '/'), f.Value)).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            Add(file, bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>As <see cref="File.ReadAllText(string)"/>: UTF-8 unless a byte order mark says otherwise.</summary>
    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
