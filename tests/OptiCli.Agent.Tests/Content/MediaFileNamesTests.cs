using OptiCli.Agent.Content;

namespace OptiCli.Agent.Tests.Content;

public class MediaFileNamesTests
{
    [Theory]
    [InlineData("report.pdf", ".pdf")]
    [InlineData(" Report.PDF ", ".pdf")]
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData("rapport q1 2026.pdf", ".pdf")]
    public void Takes_the_extension_of_a_bare_file_name(string name, string extension) =>
        Assert.Equal(extension, MediaFileNames.Extension(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("report")]
    [InlineData(".pdf")]
    [InlineData("report.")]
    [InlineData("files/report.pdf")]
    [InlineData("..\\report.pdf")]
    [InlineData("..")]
    public void Rejects_paths_and_names_without_an_extension(string? name) => Assert.Null(MediaFileNames.Extension(name));
}
