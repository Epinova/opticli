using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

/// <summary>What <c>restore</c> says about published content: only a page has a URL to be live at.</summary>
public class RestoreWarningTests
{
    [Theory]
    [InlineData(true, false, "123 has a published version, so it is live again, at its URL below 45.")]
    [InlineData(true, true, "123 has a published version: once restored it is live again, at its URL below 45.")]
    [InlineData(false, false, "123 has a published version, so it is published again, and shows wherever it is used.")]
    [InlineData(false, true, "123 has a published version: once restored it is published again, and shows wherever it is used.")]
    public void Only_a_page_is_said_to_be_live_at_its_url(bool page, bool dryRun, string expected)
    {
        Assert.Equal(expected, WriteExecutor.RestoredLiveWarning("123", page, "45", dryRun));
    }

    [Fact]
    public void A_folder_without_versions_says_what_comes_back_below_it_and_a_page_with_neither_says_nothing()
    {
        // A folder has no status of its own: its published blocks show wherever they are used again.
        Assert.Equal(["3 item(s) below it came back with it, and those with a published version are live again."],
            WriteExecutor.OutOfTrashWarnings("123", null, page: false, "45", descendants: 3, dryRun: false));
        Assert.Equal(2, WriteExecutor.OutOfTrashWarnings("123", "published", page: true, "45", descendants: 1, dryRun: true).Count());
        Assert.Empty(WriteExecutor.OutOfTrashWarnings("123", "checkedOut", page: true, "45", descendants: 0, dryRun: false));
    }
}
