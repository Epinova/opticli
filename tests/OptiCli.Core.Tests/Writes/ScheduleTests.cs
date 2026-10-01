using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class ScheduleTests
{
    private static readonly DateTimeOffset Now = new(2025, 3, 1, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2025-03-02T08:00Z", "2025-03-02T08:00:00Z")]
    [InlineData("2025-03-02T08:00", "2025-03-02T08:00:00Z")]
    [InlineData("2025-03-02T09:00+01:00", "2025-03-02T08:00:00Z")]
    public void Times_are_utc_unless_they_have_an_offset(string text, string utc)
    {
        Assert.Equal(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), PublishTimes.Parse(text, "--publish-at", Now));
    }

    [Fact]
    public void A_time_in_the_past_or_not_a_time_is_refused()
    {
        Assert.Contains("isn't in the future", Assert.Throws<UsageException>(() => PublishTimes.Parse("2025-03-01T07:59Z", "--publish-at", Now)).Message);
        Assert.Throws<UsageException>(() => PublishTimes.Parse("tomorrow", "--publish-at", Now));
    }

    [Fact]
    public void Plans_schedule_and_apply_publish_keeps_a_schedule()
    {
        var plan = WritePlan.Parse("""{"operations": [{"op": "set", "ref": "123", "name": "x", "publishAt": "2025-03-02T08:00Z"}, {"op": "publish", "ref": "124", "publishAt": "soon"}]}""".Replace("soon", "2025-03-02T08:00Z"));

        var set = Assert.IsType<SetOperation>(plan.Steps[0].Operation);
        Assert.Equal(new DateTimeOffset(2025, 3, 2, 8, 0, 0, TimeSpan.Zero), set.PublishAt);
        Assert.True(set.Publishes);
        Assert.False(((SetOperation)set.WithPublish()).Publish);
        Assert.Contains("\"publishAt\" must be a date and time", Assert.Throws<UsageException>(() => WritePlan.Parse("""{"operations": [{"op": "set", "ref": "1", "publishAt": "soon"}]}""")).Message);
    }

    [Fact]
    public void A_scheduled_version_is_undone_by_discarding_it()
    {
        var output = new WriteOutput("123", "123_13", null, "ArticlePage", "News", "en", "delayedPublish", "5", Saved: true, Published: false, DryRun: false, Valid: true,
            BaseVersion: "123_12", Changes: [], Validation: null) { ScheduledFor = new DateTime(2025, 3, 2, 8, 0, 0, DateTimeKind.Utc) };

        Assert.Equal("123_13 is scheduled to be published 2025-03-02 08:00:00Z, so nothing live changed yet; to cancel, discard it: opticli discard 123 --version 13",
            UndoHints.For(new SetOperation("123"), output));
    }
}
