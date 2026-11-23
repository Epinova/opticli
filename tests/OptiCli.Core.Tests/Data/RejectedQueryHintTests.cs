using OptiCli.Core.Data;

namespace OptiCli.Core.Tests.Data;

public class RejectedQueryHintTests
{
    [Fact]
    public void Maxrecursion_gets_a_hint_on_option_maxrecursion()
    {
        Assert.Contains("OPTION (MAXRECURSION", CmsDatabase.RejectedHint(530));
    }

    [Fact]
    public void Other_errors_point_at_the_table_names()
    {
        Assert.Contains("sys.tables", CmsDatabase.RejectedHint(208));
    }
}
