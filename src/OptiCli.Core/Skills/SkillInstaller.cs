using OptiCli.Core.Errors;

namespace OptiCli.Core.Skills;

/// <param name="Status"><c>installed</c> (new), <c>updated</c> (replaced a different copy) or <c>unchanged</c>.</param>
/// <param name="PreviousVersion">The replaced copy's <c>opticli-version</c>, when there was one.</param>
public sealed record SkillInstallResult(string Directory, string Status, string? Version, string? PreviousVersion, IReadOnlyList<string> Files);

/// <summary>An installed copy of the skill, as <c>doctor</c> reports it.</summary>
/// <param name="Outdated">True when it was written for an older opticli than the one running.</param>
public sealed record InstalledSkill(SkillScope Scope, string Path, string? Version, bool Outdated);

/// <summary>Copies the bundled skill into a skill directory and inspects installed copies.</summary>
public static class SkillInstaller
{
    /// <summary>Written next to the skill: what opticli installed, so a later install can tell its own files from local edits.</summary>
    public const string ManifestFile = ".opticli-install.json";

    /// <summary>
    /// Writes the bundle. A copy opticli installed earlier is replaced as long as its files are unchanged since (the
    /// manifest's hashes match); a copy with local edits, or one without a manifest, is only replaced with
    /// <paramref name="force"/>.
    /// </summary>
    /// <exception cref="ConflictException">A different copy that may have local edits is installed and <paramref name="force"/> is false.</exception>
    public static SkillInstallResult Install(SkillBundle bundle, string directory, bool force)
    {
        var existing = bundle.Files.Keys.Where(name => File.Exists(Path.Combine(directory, name))).ToList();
        var previousVersion = ReadInstalledVersion(directory);
        var differs = bundle.Files.Any(file => !File.Exists(Path.Combine(directory, file.Key)) || Normalize(File.ReadAllText(Path.Combine(directory, file.Key))) != Normalize(file.Value));

        if (existing.Count > 0 && !differs)
        {
            WriteManifest(bundle, directory);
            return new SkillInstallResult(directory, "unchanged", bundle.Version, previousVersion, [.. bundle.Files.Keys]);
        }
        if (existing.Count > 0 && !force)
        {
            var manifest = ReadManifest(directory);
            var edited = existing.Where(name => manifest is null || !manifest.TryGetValue(name, out var hash) || hash != Hash(File.ReadAllText(Path.Combine(directory, name)))).ToList();
            if (edited.Count > 0)
            {
                throw new ConflictException(
                    manifest is null
                        ? $"An opticli skill (opticli-version {previousVersion ?? "unknown"}) is installed in {directory}, with no record of what opticli installed there, so it may have local edits; this opticli carries {bundle.Version ?? "an unversioned one"}."
                        : $"The opticli skill in {directory} has local edits in {string.Join(", ", edited)} since opticli installed it (opticli-version {previousVersion ?? "unknown"}); this opticli carries {bundle.Version ?? "an unversioned one"}.",
                    "Run again with --force to replace it (local edits to those files are lost), or `opticli skill print` to compare first.");
            }
        }

        Directory.CreateDirectory(directory);
        foreach (var (name, content) in bundle.Files)
        {
            File.WriteAllText(Path.Combine(directory, name), content);
        }
        WriteManifest(bundle, directory);
        return new SkillInstallResult(directory, existing.Count > 0 ? "updated" : "installed", bundle.Version, existing.Count > 0 ? previousVersion : null, [.. bundle.Files.Keys]);
    }

    /// <summary>Of the content with LF line endings, so a copy checked out with CRLF (Windows) doesn't count as edited.</summary>
    private static string Hash(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(content)))).ToLowerInvariant();

    private static string Normalize(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void WriteManifest(SkillBundle bundle, string directory)
    {
        var manifest = new { version = bundle.Version, files = bundle.Files.ToDictionary(f => f.Key, f => Hash(f.Value)) };
        File.WriteAllText(Path.Combine(directory, ManifestFile), System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <returns>File name to hash of what opticli wrote; null when there is no readable manifest.</returns>
    private static Dictionary<string, string>? ReadManifest(string directory)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, ManifestFile)));
            return document.RootElement.GetProperty("files").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <returns>Null when no skill is installed in <paramref name="directory"/>.</returns>
    public static InstalledSkill? Inspect(SkillScope scope, string directory, string runningVersion)
    {
        var main = Path.Combine(directory, SkillBundle.MainFile);
        if (!File.Exists(main))
        {
            return null;
        }
        var version = ReadInstalledVersion(directory);
        // An unversioned copy predates versioned skills, so it counts as outdated too.
        var outdated = version is null || ToolVersions.Compare(version, runningVersion) < 0;
        return new InstalledSkill(scope, main, version, outdated);
    }

    private static string? ReadInstalledVersion(string directory)
    {
        var main = Path.Combine(directory, SkillBundle.MainFile);
        try
        {
            return File.Exists(main) ? SkillBundle.ReadVersion(File.ReadAllText(main)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
