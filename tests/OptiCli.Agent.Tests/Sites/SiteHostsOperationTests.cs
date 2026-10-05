using System.Globalization;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Http;
using OptiCli.Agent.Sites;
using OptiCli.Cms;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Sites;

/// <summary>Saving through a stand-in for the CMS's site definition repository, which can fail on a given site.</summary>
public class SiteHostsOperationTests
{
    private static readonly Guid A = Guid.Parse("0b1c2d3e-0000-4000-8000-00000000000a");
    private static readonly Guid B = Guid.Parse("0b1c2d3e-0000-4000-8000-00000000000b");

    private static SiteDefinition Site(Guid id, string name, string host) => new()
    {
        Id = id,
        Name = name,
        SiteUrl = new Uri($"https://{host}/"),
        StartPage = new ContentReference(5),
        SiteAssetsRoot = new ContentReference(3),
        Hosts = [new HostDefinition { Name = host, Type = HostDefinitionType.Primary }],
    };

    private static SiteHostChange Primary(string site, string host) => new() { Site = site, Host = host, Action = SiteHostActions.Primary };

    private static (SiteHostsResult? Result, AgentException? Error) Run(SiteRepository repository, params SiteHostChange[] changes)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISiteDefinitionRepository>(repository)
            .AddSingleton<ILanguageBranchRepository>(new Languages())
            .AddSingleton(Settings())
            .BuildServiceProvider();
        var request = new AgentRequest(new DefaultHttpContext { RequestServices = services }, null);
        try
        {
            return (SiteHostsOperation.Run(request, new SiteHostsRequest { Changes = changes }), null);
        }
        catch (AgentException ex)
        {
            return (null, ex);
        }
    }

    [Fact]
    public void Every_changed_site_is_saved_with_the_planned_hosts_and_site_url()
    {
        var repository = new SiteRepository(Site(A, "Site A", "site-a.example"), Site(B, "Site B", "site-b.example"));

        var (result, _) = Run(repository, Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:443"));

        Assert.True(result!.Saved);
        Assert.Equal(["Site A", "Site B"], repository.SavedNames);
        Assert.Equal("https://localhost/", repository.Get(B).SiteUrl.ToString());
        Assert.Equal(["site-b.example", "localhost"], repository.Get(B).Hosts.Select(h => h.Name));
        Assert.Contains(result.Warnings!, w => w.Contains("restart the site", StringComparison.Ordinal));
    }

    [Fact]
    public void A_site_the_cms_refuses_after_another_was_saved_says_which_were_saved_in_the_message()
    {
        var repository = new SiteRepository(Site(A, "Site A", "site-a.example"), Site(B, "Site B", "site-b.example"))
        {
            Refuse = (B, new ArgumentException("'localhost' is already used in site 'Site A'.")),
        };

        var (_, error) = Run(repository, Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5002"));

        Assert.Equal(AgentErrorCodes.Validation, error!.Code);
        Assert.Contains("Site B: the CMS refused the site's hosts: 'localhost' is already used in site 'Site A'", error.Message);
        Assert.EndsWith("Already saved: Site A; not saved: Site B.", error.Message);
        Assert.DoesNotContain("nothing was saved", error.Hint);
        Assert.Equal(["Site A"], repository.SavedNames);
    }

    [Fact]
    public void Any_other_failure_after_a_save_says_so_too()
    {
        var repository = new SiteRepository(Site(A, "Site A", "site-a.example"), Site(B, "Site B", "site-b.example"))
        {
            Refuse = (B, new InvalidOperationException("The database went away.")),
        };

        var (_, error) = Run(repository, Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5002"));

        Assert.Equal(AgentErrorCodes.Internal, error!.Code);
        Assert.Equal("The database went away. Already saved: Site A; not saved: Site B.", error.Message);
        Assert.Contains(typeof(InvalidOperationException).FullName!, error.Hint);
    }

    [Fact]
    public void A_site_deleted_meanwhile_stops_the_batch_before_any_site_is_saved()
    {
        var repository = new SiteRepository(Site(A, "Site A", "site-a.example"), Site(B, "Site B", "site-b.example")) { Vanished = B };

        var (_, error) = Run(repository, Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5002"));

        Assert.Equal(AgentErrorCodes.NotFound, error!.Code);
        Assert.Contains("nothing was saved", error.Message);
        Assert.Empty(repository.SavedNames);
    }

    /// <summary>The repository's contract as the operation uses it: List, Get, Save.</summary>
    private sealed class SiteRepository(params SiteDefinition[] sites) : ISiteDefinitionRepository
    {
        private readonly List<SiteDefinition> _sites = [.. sites];

        public (Guid Site, Exception Failure)? Refuse { get; init; }

        /// <summary>Listed, but gone by the time the operation loads it to save.</summary>
        public Guid? Vanished { get; init; }

        public List<string> SavedNames { get; } = [];

        public event EventHandler<EventArgs>? SiteDefinitionChanged;

        public IEnumerable<SiteDefinition> List() => _sites;

        public SiteDefinition Get(Guid id) => id == Vanished ? null! : _sites.Single(s => s.Id == id);

        public void Save(SiteDefinition siteDefinition)
        {
            if (Refuse is { } refuse && refuse.Site == siteDefinition.Id)
            {
                throw refuse.Failure;
            }
            _sites[_sites.FindIndex(s => s.Id == siteDefinition.Id)] = siteDefinition;
            SavedNames.Add(siteDefinition.Name);
            SiteDefinitionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Delete(Guid id) => throw new NotSupportedException();
    }

    private sealed class Languages : ILanguageBranchRepository
    {
        public IList<LanguageBranch> ListEnabled() => [new LanguageBranch("en"), new LanguageBranch("nb")];

        public IList<LanguageBranch> ListAll() => ListEnabled();

        public LanguageBranch Load(CultureInfo language) => throw new NotSupportedException();

        public LanguageBranch Load(int id) => throw new NotSupportedException();

        public LanguageBranch LoadFirstEnabledBranch() => throw new NotSupportedException();

        public void Delete(int id) => throw new NotSupportedException();

        public void Save(LanguageBranch languageBranch) => throw new NotSupportedException();
    }
}
