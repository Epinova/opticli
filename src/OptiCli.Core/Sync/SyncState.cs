using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Sync;

/// <summary>One language branch as the last sync left it.</summary>
/// <param name="Source">The source's version the sync read (the one written, or found the same).</param>
/// <param name="Published">The source's branch was published.</param>
/// <param name="Target">The target's newest version of the branch once the sync was done: a newer one is a local change.</param>
/// <param name="TargetSaved">When that version was saved, so a version with the same id saved later (the target restored from a backup, then edited) is a local change too; null when not known.</param>
public sealed record SyncedBranch(int Source, bool Published, int Target, DateTime? TargetSaved = null);

/// <summary>One item as the last sync left it.</summary>
/// <param name="Source">Where the item was in the source: its parent's GUID, or <c>for:&lt;owner GUID&gt;</c> in a "For this page" folder.</param>
/// <param name="Target">Where it was in the target, likewise: somewhere else now is a local move.</param>
/// <param name="Branches">By language (<c>*</c> for content without languages).</param>
/// <param name="Trashed">The sync moved it to the target's recycle bin (<c>--delete</c>), so it being there isn't a local change.</param>
public sealed record SyncedItem(string? Source, string? Target, IReadOnlyDictionary<string, SyncedBranch> Branches, bool Trashed = false);

/// <summary>
/// What a sync from one database into another last left behind, per item, so the next one only reads and writes what
/// changed (<c>--since</c> is implicit), knows what was synced before (for <c>--delete</c>), and tells local changes from
/// the source's (a conflict when both changed). Kept in the user config folder (next to <c>config.json</c>), one file per
/// project, source and target database. Only what a sync saved goes in: Ctrl+C or a failed step leaves the items it
/// didn't finish as they were, so the next sync looks at them again.
/// </summary>
public sealed class SyncState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private sealed record StoredDatabase(string Server, string Database);

    private sealed record Stored(string Project, StoredDatabase Source, StoredDatabase Target, DateTime? Synced, Dictionary<Guid, SyncedItem>? Items);

    private SyncState(string path, string project, DatabaseKey source, DatabaseKey target, DateTime? synced, Dictionary<Guid, SyncedItem> items)
    {
        Path = path;
        Project = project;
        Source = source;
        Target = target;
        Synced = synced;
        Items = items;
    }

    /// <summary>The file it is kept in.</summary>
    public string Path { get; }

    public string Project { get; }

    public DatabaseKey Source { get; }

    public DatabaseKey Target { get; }

    /// <summary>When the last sync finished; null before the first (every item is then compared: the baseline).</summary>
    public DateTime? Synced { get; private set; }

    public Dictionary<Guid, SyncedItem> Items { get; }

    /// <summary>The state's file: <c>sync/&lt;hash of project, source and target&gt;.json</c> in the user config folder.</summary>
    public static string PathFor(OptiCliEnvironment environment, string project, DatabaseKey source, DatabaseKey target)
    {
        // As the serve state keys a project: the same folder typed in another case on Windows is the same project (another
        // key would be a new baseline, which writes over local changes).
        var directory = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(project));
        var identity = string.Join("\n", OperatingSystem.IsWindows() ? directory.ToUpperInvariant() : directory, source.Server, source.Database, target.Server, target.Database);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(environment.UserConfigFile)!, "sync", $"{hash}.json");
    }

    /// <summary>The state in <paramref name="path"/>; an empty one (no sync yet) when there is no file.</summary>
    /// <exception cref="UsageException">The file can't be read as a sync state.</exception>
    public static SyncState Load(string path, string project, DatabaseKey source, DatabaseKey target)
    {
        if (!File.Exists(path))
        {
            return new SyncState(path, project, source, target, null, []);
        }
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) ?? throw new JsonException("The file is empty.");
            return new SyncState(path, project, source, target, stored.Synced, stored.Items ?? []);
        }
        catch (JsonException ex)
        {
            throw new UsageException($"The sync state {path} can't be read: {ex.Message}",
                "Deleting it starts over: the next sync then compares every item again (a new baseline), and writes the source's content over published local changes since the last sync (they stay in the history). Run that sync with --dry-run first.");
        }
    }

    /// <summary>Writes the state (through a temporary file, so an interrupted write leaves the previous one).</summary>
    public void Save(DateTime synced)
    {
        Synced = synced;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var stored = new Stored(Project, new StoredDatabase(Source.Server, Source.Database), new StoredDatabase(Target.Server, Target.Database), synced, Items);
        UserConfig.Replace(Path, JsonSerializer.Serialize(stored, Json) + Environment.NewLine);
    }
}
