using OptiCli.Core.Data;

namespace OptiCli.Core.Tests.Data;

public class SqlReaderExtensionsTests
{
    [Fact]
    public void A_datetime2_is_rounded_to_the_millisecond()
    {
        // CMS 13's datetime2 keeps 7 decimals; CMS 12's datetime never gave more than milliseconds.
        var stored = new DateTime(2026, 10, 6, 17, 12, 37, DateTimeKind.Utc).AddTicks(2_645_000);

        Assert.Equal(new DateTime(2026, 10, 6, 17, 12, 37, 265, DateTimeKind.Utc), SqlReaderExtensions.ToMilliseconds(stored));
        Assert.Equal(new DateTime(2026, 10, 6, 17, 12, 37, 264, DateTimeKind.Utc), SqlReaderExtensions.ToMilliseconds(stored.AddTicks(-1)));
    }

    [Fact]
    public void A_whole_millisecond_is_kept()
    {
        var time = new DateTime(2026, 10, 6, 17, 12, 37, 264, DateTimeKind.Utc);

        Assert.Equal(time, SqlReaderExtensions.ToMilliseconds(time));
        Assert.Equal(DateTimeKind.Utc, SqlReaderExtensions.ToMilliseconds(time).Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4_999)]
    [InlineData(5_000)]
    [InlineData(9_999)]
    public void The_last_millisecond_there_is_doesnt_overflow(int ticksIntoIt)
    {
        // DateTime.MaxValue, stored in a datetime2, comes back as 9999-12-31 23:59:59.9999999.
        var lastMillisecond = new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc);

        Assert.Equal(lastMillisecond, SqlReaderExtensions.ToMilliseconds(lastMillisecond.AddTicks(ticksIntoIt)));
    }
}
