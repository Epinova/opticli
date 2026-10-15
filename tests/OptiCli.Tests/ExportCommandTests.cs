namespace OptiCli.Tests;

/// <summary><c>export</c>'s own usage rules, which it checks before it reads anything.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class ExportCommandTests
{
    [Theory]
    [InlineData("export", "Give refs, --site <name> or --all")]
    [InlineData("export 1 --all", "Give refs, --site <name> or --all")]
    [InlineData("export --site Alloy --all", "Give refs, --site <name> or --all")]
    [InlineData("export --all --descendants", "--descendants is for a ref")]
    [InlineData("export 1 --languages sv --all-languages", "not both")]
    [InlineData("export 1 --no-media --blobs .", "--no-media reads no files")]
    [InlineData("export 1 --blobs http://account.blob.core.windows.net/media?sig=secret", "https://")]
    [InlineData("export 1 --blobs ./no-such-blob-folder", "doesn't exist")]
    public async Task What_export_is_given_is_checked_first(string line, string message)
    {
        var run = await Opticli.RunAsync([.. line.Split(' '), "--json"]);

        Assert.True(run.ExitCode == 1, run.ToString());
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
        Assert.Contains(message, run.Error().GetProperty("message").GetString());
        Assert.DoesNotContain("secret", run.Stdout);
    }

    [Fact]
    public void Out_may_be_new_empty_or_an_earlier_exports_folder_never_one_with_a_plan_of_its_own()
    {
        var root = Directory.CreateTempSubdirectory("opticli-export-out-").FullName;
        try
        {
            var now = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
            Assert.Equal(Path.Combine(root, "new"), Commands.ExportCommand.Folder("new", root, "db", now));
            Assert.True(Directory.Exists(Path.Combine(root, "new")));
            Assert.Equal(Path.Combine(root, "export-db-20261015-080000"), Commands.ExportCommand.Folder(null, root, "db", now));

            // A plan folder of the user's own: its plan and files stay.
            var own = Directory.CreateDirectory(Path.Combine(root, "own")).FullName;
            File.WriteAllText(Path.Combine(own, "plan.json"), """{"operations": [{"op": "publish", "ref": "1"}]}""");
            Directory.CreateDirectory(Path.Combine(own, "files"));
            File.WriteAllText(Path.Combine(own, "files", "keep.png"), "x");
            var refused = Assert.Throws<Core.Errors.UsageException>(() => Commands.ExportCommand.Folder("own", root, "db", now));
            Assert.Contains("isn't an earlier export's folder", refused.Message);
            Assert.True(File.Exists(Path.Combine(own, "files", "keep.png")));
            Assert.Throws<Core.Errors.UsageException>(() => Commands.ExportCommand.Folder(Path.Combine("own", "plan.json"), root, "db", now));

            // An earlier export's folder: emptied of what an export writes, the rest kept.
            var earlier = Directory.CreateDirectory(Path.Combine(root, "earlier")).FullName;
            File.WriteAllText(Path.Combine(earlier, "plan.json"), """{"source": {"readAt": "2026-10-15T08:00:00Z", "opticli": "0.18.0"}, "operations": []}""");
            Directory.CreateDirectory(Path.Combine(earlier, "texts"));
            File.WriteAllText(Path.Combine(earlier, "texts", "a.html"), "x");
            File.WriteAllText(Path.Combine(earlier, "notes.md"), "x");
            Assert.Equal(earlier, Commands.ExportCommand.Folder("earlier", root, "db", now));
            Commands.ExportCommand.Clear(earlier);
            Assert.Equal(["notes.md"], Directory.EnumerateFileSystemEntries(earlier).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
