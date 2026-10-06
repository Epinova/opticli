using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class AgentSelectionTests
{
    private static readonly CmsSchema Cms12 = CmsSchema.Cms12 with { Version = 8023 };
    private static readonly CmsSchema Cms13 = CmsSchema.Cms13 with { Version = 21005 };

    /// <summary>A database the CMS hasn't created its schema in yet (a new site's).</summary>
    private static readonly CmsSchema Empty = CmsSchema.Cms12;

    [Theory]
    [InlineData("12.21.2", 12)]
    [InlineData("13.3.0", 13)]
    [InlineData("13.0.0-preview1", 13)]
    public void The_project_decides(string version, int major)
    {
        Assert.Equal(new AgentChoice(major, "project"), AgentSelection.Choose(version, major == 13 ? Cms13 : Cms12, "db"));
        Assert.Equal(new AgentChoice(major, "project"), AgentSelection.Choose(version, Empty, "db"));
        Assert.Equal(new AgentChoice(major, "project"), AgentSelection.Choose(version, null, "db", "unreachable"));
    }

    [Fact]
    public void An_unrestored_project_falls_back_to_the_database()
    {
        Assert.Equal(new AgentChoice(12, "database"), AgentSelection.Choose(null, Cms12, "db"));
        Assert.Equal(new AgentChoice(13, "database"), AgentSelection.Choose(null, Cms13, "db"));
        Assert.Equal(new AgentChoice(13, "database"), AgentSelection.Choose("$(CmsVersion)", Cms13, "db"));
        // Without sp_DatabaseVersion, the applications tables still say CMS 13.
        Assert.Equal(new AgentChoice(13, "database"), AgentSelection.Choose(null, Empty with { Applications = true }, "db"));
    }

    [Fact]
    public void A_cms_13_project_on_a_cms_12_database_is_refused_before_the_site_upgrades_it()
    {
        var error = Assert.Throws<RefusedException>(() => AgentSelection.Choose("13.3.0", Cms12, "alloy"));

        Assert.Contains("CMS 13 (13.3.0)", error.Message);
        Assert.Contains("database 'alloy' has a CMS 12 schema (version 8023)", error.Message);
        Assert.Contains("upgrade the database to CMS 13", error.Hint);
    }

    [Fact]
    public void A_cms_12_project_on_a_cms_13_database_is_refused()
    {
        var error = Assert.Throws<RefusedException>(() => AgentSelection.Choose("12.21.2", Cms13, null));

        Assert.Contains("the database has a CMS 13 schema", error.Message);
        Assert.Contains("CMS 12 can't start against it", error.Hint);
    }

    [Fact]
    public void Neither_telling_is_a_usage_error()
    {
        var empty = Assert.Throws<UsageException>(() => AgentSelection.Choose(null, Empty, "new"));
        Assert.Contains("the database 'new' has no CMS schema yet", empty.Message);
        Assert.Contains("dotnet restore", empty.Hint);

        var unreachable = Assert.Throws<UsageException>(() => AgentSelection.Choose(null, null, "new", "Could not connect"));
        Assert.Contains("couldn't be read (Could not connect)", unreachable.Message);
    }

    [Fact]
    public void Other_majors_are_refused()
    {
        var error = Assert.Throws<UsageException>(() => AgentSelection.Choose("14.0.0", Empty, "db"));

        Assert.Contains("CMS 14 (14.0.0)", error.Message);
        Assert.Contains("CMS 12 and 13 only", error.Message);
    }

    [Theory]
    [InlineData("13.3.0", 13, false)]
    [InlineData("12.21.2", 12, false)]
    [InlineData("13.3.0", 12, true)]
    [InlineData("12.21.2", 13, true)]
    [InlineData(null, 13, false)]
    public void The_started_site_must_run_the_agents_cms_major(string? cmsVersion, int agent, bool mismatch)
    {
        var problem = SiteLauncher.AgentMismatch(cmsVersion, agent);

        Assert.Equal(mismatch, problem is not null);
        if (problem is not null)
        {
            Assert.Contains($"runs CMS {cmsVersion}", problem);
            Assert.Contains($"CMS {agent} agent", problem);
        }
    }
}
