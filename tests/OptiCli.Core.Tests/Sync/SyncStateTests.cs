using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Sync;

namespace OptiCli.Core.Tests.Sync;

/// <summary>The sync state: one file per project, source and target in the user config folder, written whole.</summary>
public sealed class SyncStateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("opticli-sync-state-").FullName;

    private static readonly DatabaseKey Source = DatabaseKey.Of("prod.example.net", "Cms")!;
    private static readonly DatabaseKey Target = DatabaseKey.Of("localhost,1433", "cms-local")!;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private OptiCliEnvironment Environment() =>
        new(_root, new Dictionary<string, string> { ["HOME"] = _root, ["XDG_CONFIG_HOME"] = Path.Combine(_root, "config") });

    [Fact]
    public void Each_project_source_and_target_has_a_file_of_its_own_next_to_the_user_config()
    {
        var environment = Environment();
        var project = Path.Combine(_root, "site");

        var path = SyncState.PathFor(environment, project, Source, Target);

        Assert.Equal(Path.Combine(Path.GetDirectoryName(environment.UserConfigFile)!, "sync"), Path.GetDirectoryName(path));
        Assert.Equal(path, SyncState.PathFor(environment, project, DatabaseKey.Of("PROD.example.net", "cms")!, DatabaseKey.Of("127.0.0.1", "CMS-LOCAL")!));
        Assert.NotEqual(path, SyncState.PathFor(environment, Path.Combine(_root, "other"), Source, Target));
        Assert.NotEqual(path, SyncState.PathFor(environment, project, Target, Source));
    }

    [Fact]
    public void A_state_is_empty_before_the_first_sync_and_reads_back_what_was_saved()
    {
        var path = SyncState.PathFor(Environment(), _root, Source, Target);
        var item = Guid.NewGuid();

        var state = SyncState.Load(path, _root, Source, Target);
        Assert.Null(state.Synced);
        Assert.Empty(state.Items);

        state.Items[item] = new SyncedItem("parent", $"for:{Guid.Empty}", new Dictionary<string, SyncedBranch> { ["en"] = new(10, true, 20), ["*"] = new(3, false, 4) });
        state.Items[Guid.NewGuid()] = new SyncedItem(null, null, new Dictionary<string, SyncedBranch>(), Trashed: true);
        var synced = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        state.Save(synced);

        var read = SyncState.Load(path, _root, Source, Target);
        Assert.Equal(synced, read.Synced);
        Assert.Equal(2, read.Items.Count);
        Assert.Equal(new SyncedBranch(10, true, 20), read.Items[item].Branches["en"]);
        Assert.Equal(("parent", $"for:{Guid.Empty}", false), (read.Items[item].Source, read.Items[item].Target, read.Items[item].Trashed));
        Assert.Single(read.Items.Values, i => i.Trashed);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void A_state_that_cant_be_read_says_how_to_start_over()
    {
        var path = SyncState.PathFor(Environment(), _root, Source, Target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var refused = Assert.Throws<UsageException>(() => SyncState.Load(path, _root, Source, Target));

        Assert.Contains(path, refused.Message, StringComparison.Ordinal);
        Assert.Contains("baseline", refused.Hint, StringComparison.Ordinal);
    }
}
