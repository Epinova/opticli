using OptiCli.Core.Errors;
using OptiCli.Core.Seals;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Seals;

/// <summary>
/// The plan and the files it reads, read once and hashed: the hash changes with any byte or name, and a run of the snapshot
/// never reads a file from the disk again, so one swapped after the hash isn't what runs.
/// </summary>
public sealed class PlanSnapshotTests : IDisposable
{
    private const string Plan = """
        {"guidNamespace": "4b2c7f0e-2a59-4f0c-9d5e-6f1a2b3c4d5e", "operations": [
          {"op": "create", "id": "news", "parent": "7b08c7d5-7585-47e5-add7-5822da68c3ce", "type": "StandardPage", "name": "News", "properties": {"MainBody": "@texts/body.html", "Note": "@@literal"}}]}
        """;

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private async Task<PlanSnapshot> ReadAsync() => await PlanSnapshot.ReadAsync(_folder.Combine("plan.json"), CancellationToken.None);

    [Fact]
    public async Task The_hash_covers_the_plan_and_every_file_it_reads_and_nothing_else()
    {
        _folder.Write("plan.json", Plan);
        _folder.Write("texts/body.html", "<p>Body</p>");
        _folder.Write("texts/unused.html", "<p>Not read</p>");

        var first = await ReadAsync();
        _folder.Write("texts/unused.html", "<p>Changed, still not read</p>");
        var same = await ReadAsync();
        _folder.Write("texts/body.html", "<p>Body!</p>");
        var fileChanged = await ReadAsync();
        _folder.Write("texts/body.html", "<p>Body</p>");
        _folder.Write("plan.json", Plan.Replace("\"News\"", "\"News \"", StringComparison.Ordinal));
        var planChanged = await ReadAsync();

        Assert.Matches("^[0-9a-f]{64}$", first.Hash);
        Assert.Equal(first.Hash, same.Hash);
        Assert.NotEqual(first.Hash, fileChanged.Hash);
        Assert.NotEqual(first.Hash, planChanged.Hash);
        Assert.Equal([_folder.Combine("texts/body.html")], first.Files);
    }

    [Fact]
    public async Task The_same_bytes_under_another_name_are_another_plan()
    {
        _folder.Write("plan.json", Plan);
        _folder.Write("texts/body.html", "<p>Body</p>");
        var first = await ReadAsync();
        _folder.Write("plan.json", Plan.Replace("texts/body.html", "texts/other.html", StringComparison.Ordinal));
        _folder.Write("texts/other.html", "<p>Body</p>");

        Assert.NotEqual(first.Hash, (await ReadAsync()).Hash);
    }

    [Fact]
    public async Task A_run_reads_the_files_from_the_snapshot_not_from_the_disk()
    {
        _folder.Write("plan.json", Plan);
        _folder.Write("texts/body.html", "<p>Hashed</p>");
        var snapshot = await ReadAsync();
        _folder.Write("texts/body.html", "<p>Swapped after the hash</p>");

        var steps = PlanFiles.Resolve(snapshot.Plan.Steps, snapshot.Directory, allowOutside: false, snapshot);

        var create = Assert.IsType<CreateOperation>(steps.Single().Operation);
        Assert.Equal("<p>Hashed</p>", (string?)create.Properties!["MainBody"]);
        Assert.Equal("@literal", (string?)create.Properties!["Note"]);
        Assert.Equal("<p>Hashed</p>", snapshot.Text(_folder.Combine("texts/body.html")));
        Assert.Null(snapshot.Text(_folder.Combine("texts/elsewhere.html")));
    }

    [Fact]
    public async Task A_missing_file_or_one_outside_the_folder_is_refused()
    {
        _folder.Write("plan.json", Plan);
        var missing = await Assert.ThrowsAsync<UsageException>(ReadAsync);
        _folder.Write("plan.json", Plan.Replace("@texts/body.html", "@../outside.html", StringComparison.Ordinal));
        var outside = await Assert.ThrowsAsync<UsageException>(ReadAsync);
        File.Delete(_folder.Combine("plan.json"));

        Assert.Contains("don't exist", missing.Message, StringComparison.Ordinal);
        Assert.Contains("outside", outside.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<NotFoundException>(ReadAsync);
    }
}
