using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

public class MediaFilesTests
{
    [Fact]
    public void Plan_files_are_relative_to_the_plan_folder()
    {
        using var root = new TempDirectory();
        var file = root.Write("plan/files/report.pdf", "%PDF");

        Assert.Equal(file, MediaFiles.ForPlan("files/report.pdf", root.Combine("plan"), allowOutside: false));
        Assert.Equal(file, MediaFiles.ForPlan("./files/../files/report.pdf", root.Combine("plan") + Path.DirectorySeparatorChar, allowOutside: false));
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("files/../../outside.pdf")]
    [InlineData("..")]
    public void Plan_files_may_not_escape_the_plan_folder(string file)
    {
        using var root = new TempDirectory();
        root.Write("outside.pdf", "%PDF");

        var error = Assert.Throws<UsageException>(() => MediaFiles.ForPlan(file, root.Combine("plan"), allowOutside: false));
        Assert.Contains("outside the plan's folder", error.Message);
        Assert.Contains("--allow-outside", error.Hint);
    }

    [Fact]
    public void Absolute_plan_files_are_refused()
    {
        using var root = new TempDirectory();
        var inside = root.Write("plan/report.pdf", "%PDF");

        Assert.Contains("absolute path", Assert.Throws<UsageException>(() => MediaFiles.ForPlan(inside, root.Combine("plan"), allowOutside: false)).Message);
    }

    [Fact]
    public void A_link_out_of_the_plan_folder_is_refused()
    {
        using var root = new TempDirectory();
        var outside = root.Write("secret.pdf", "%PDF");
        Directory.CreateDirectory(root.Combine("plan"));
        File.CreateSymbolicLink(root.Combine("plan/link.pdf"), outside);

        Assert.Contains("links to", Assert.Throws<UsageException>(() => MediaFiles.ForPlan("link.pdf", root.Combine("plan"), allowOutside: false)).Message);
    }

    [Fact]
    public void A_linked_directory_out_of_the_plan_folder_is_refused()
    {
        using var root = new TempDirectory();
        root.Write("secrets/key.txt", "secret");
        Directory.CreateDirectory(root.Combine("plan"));
        Directory.CreateSymbolicLink(root.Combine("plan/assets"), root.Combine("secrets"));

        Assert.Contains("links to", Assert.Throws<UsageException>(() => MediaFiles.ForPlan("assets/key.txt", root.Combine("plan"), allowOutside: false)).Message);
    }

    [Fact]
    public void Links_that_stay_inside_the_plan_folder_are_fine()
    {
        using var root = new TempDirectory();
        root.Write("real/plan/files/report.pdf", "%PDF");
        Directory.CreateSymbolicLink(root.Combine("plan"), root.Combine("real/plan"));
        Directory.CreateSymbolicLink(root.Combine("real/plan/current"), "files");

        Assert.Equal(root.Combine("plan/current/report.pdf"), MediaFiles.ForPlan("current/report.pdf", root.Combine("plan"), allowOutside: false));
    }

    [Fact]
    public void Allow_outside_accepts_any_path()
    {
        using var root = new TempDirectory();
        var outside = root.Write("outside.pdf", "%PDF");

        Assert.Equal(outside, MediaFiles.ForPlan("../outside.pdf", root.Combine("plan"), allowOutside: true));
        Assert.Equal(outside, MediaFiles.ForPlan(outside, root.Combine("plan"), allowOutside: true));
    }

    [Fact]
    public void Check_follows_links_to_the_file()
    {
        using var root = new TempDirectory();
        var target = root.Write("real.pdf", "%PDF-1.4");
        File.CreateSymbolicLink(root.Combine("link.pdf"), target);

        var file = MediaFiles.Check(root.Combine("link.pdf"));

        Assert.Equal(target, file.FullName);
        Assert.Equal(8, file.Length);
    }

    [Fact]
    public void Check_refuses_missing_files_directories_empty_files_and_dangling_links()
    {
        using var root = new TempDirectory();
        root.Write("empty.pdf", "");
        Directory.CreateDirectory(root.Combine("folder"));
        File.CreateSymbolicLink(root.Combine("dangling.pdf"), root.Combine("gone.pdf"));

        Assert.Contains("does not exist", Assert.Throws<UsageException>(() => MediaFiles.Check(root.Combine("missing.pdf"))).Message);
        Assert.Contains("directory", Assert.Throws<UsageException>(() => MediaFiles.Check(root.Combine("folder"))).Message);
        Assert.Contains("empty", Assert.Throws<UsageException>(() => MediaFiles.Check(root.Combine("empty.pdf"))).Message);
        Assert.Throws<UsageException>(() => MediaFiles.Check(root.Combine("dangling.pdf")));
    }

    [Fact]
    public void Check_refuses_files_over_the_cap_without_reading_them()
    {
        using var root = new TempDirectory();
        var path = root.Combine("big.pdf");
        using (var stream = File.Create(path))
        {
            stream.SetLength(UploadRequest.MaxBytes + 1L);
        }

        var error = Assert.Throws<UsageException>(() => MediaFiles.Check(path));
        Assert.Contains("at most 50 MB", error.Message);
    }
}
