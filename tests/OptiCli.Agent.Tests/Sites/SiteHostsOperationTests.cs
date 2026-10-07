using System.Globalization;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Http;
using OptiCli.Agent.Sites;
using OptiCli.Cms;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;
#if CMS13
using System.ComponentModel.DataAnnotations;
using EPiServer.Applications;
using EPiServer.Validation;
#else
using EPiServer.Web;
#endif

namespace OptiCli.Agent.Tests.Sites;

/// <summary>
/// Saving through a stand-in for the CMS's repository, which can fail on a given site: CMS 12's site definition
/// repository, CMS 13's application repository.
/// </summary>
public class SiteHostsOperationTests
{
    private static SiteHostChange Primary(string site, string host) => new() { Site = site, Host = host, Action = SiteHostActions.Primary };

    private static SiteHostChange Add(string site, string host) => new() { Site = site, Host = host, Action = SiteHostActions.Add };

    private static SiteHostChange Remove(string site, string host) => new() { Site = site, Host = host, Action = SiteHostActions.Remove };

    private static async Task<(SiteHostsResult? Result, AgentException? Error)> Run(Action<IServiceCollection> repository, SiteHostChange[] changes, bool dryRun = false)
    {
        var services = new ServiceCollection()
            .AddSingleton<ILanguageBranchRepository>(new Languages())
            .AddSingleton(Settings());
        repository(services);
        var request = new AgentRequest(new DefaultHttpContext { RequestServices = services.BuildServiceProvider() }, null);
        try
        {
            return (await SiteHostsOperation.RunAsync(request, new SiteHostsRequest { Changes = changes, DryRun = dryRun }), null);
        }
        catch (AgentException ex)
        {
            return (null, ex);
        }
    }

#if !CMS13
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

    private static Task<(SiteHostsResult? Result, AgentException? Error)> Run(SiteRepository repository, params SiteHostChange[] changes) =>
        Run(services => services.AddSingleton<ISiteDefinitionRepository>(repository), changes);

    [Fact]
    public async Task Every_changed_site_is_saved_with_the_planned_hosts_and_site_url()
    {
        var repository = new SiteRepository(Site(A, "Site A", "site-a.example"), Site(B, "Site B", "site-b.example"));

        var (result, _) = await Run(repository, Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:443"));

        Assert.True(result!.Saved);
        Assert.Equal(["Site A", "Site B"], repository.SavedNames);
        Assert.Equal("https://localhost/", repository.Get(B).SiteUrl.ToString());
        Assert.Equal(["site-b.example", "localhost"], repository.Get(B).Hosts.Select(h => h.Name));
        Assert.Contains(result.Warnings!, w => w.Contains("restart the site", StringComparison.Ordinal) && w.Contains("cached site definitions", StringComparison.Ordinal));
        // What CMS 12 always answered: the GUID, and nothing of CMS 13's.
        Assert.Equal((A, null, null), (result.Sites[0].Id, result.Sites[0].Application, result.Sites[0].IsDefault));
    }

    [Fact]
    public async Task A_site_the_cms_refuses_after_another_was_saved_says_which_were_saved_in_the_message()
    {
        var repository = new SiteRepository(Site(A, "Site A", "site-a.example"), Site(B, "Site B", "site-b.example"))
        {
            Refuse = (B, new ArgumentException("'localhost' is already used in site 'Site A'.")),
        };

        var (_, error) = await Run(repository, Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5002"));

        Assert.Equal(AgentErrorCodes.Validation, error!.Code);
        Assert.Contains("Site B: the CMS refused the site's hosts: 'localhost' is already used in site 'Site A'", error.Message);
        Assert.EndsWith("Already saved: Site A; not saved: Site B.", error.Message);
        Assert.DoesNotContain("nothing was saved", error.Hint);
        Assert.Equal(["Site A"], repository.SavedNames);
    }

    [Fact]
    public async Task Any_other_failure_after_a_save_says_so_too()
    {
        var repository = new SiteRepository(Site(A, "Site A", "site-a.example"), Site(B, "Site B", "site-b.example"))
        {
            Refuse = (B, new InvalidOperationException("The database went away.")),
        };

        var (_, error) = await Run(repository, Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5002"));

        Assert.Equal(AgentErrorCodes.Internal, error!.Code);
        Assert.Equal("The database went away. Already saved: Site A; not saved: Site B.", error.Message);
        Assert.Contains(typeof(InvalidOperationException).FullName!, error.Hint);
    }

    [Fact]
    public async Task A_site_deleted_meanwhile_stops_the_batch_before_any_site_is_saved()
    {
        var repository = new SiteRepository(Site(A, "Site A", "site-a.example"), Site(B, "Site B", "site-b.example")) { Vanished = B };

        var (_, error) = await Run(repository, Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5002"));

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
#else
    /// <summary>An application as a CMS 13 site has it: a name that differs from its display name, and an https primary host.</summary>
    private static InProcessWebsite Site(string name, string displayName, string host, bool isDefault = false)
    {
        var application = new InProcessWebsite(name, new ContentReference(5)) { DisplayName = displayName };
        application.Hosts.Add(new ApplicationHost(host) { Type = ApplicationHostType.Primary, PreferredUrlScheme = UrlScheme.Https });
        Applications.SetDefault(application, isDefault);
        return application;
    }

    private static Task<(SiteHostsResult? Result, AgentException? Error)> Run(Applications repository, params SiteHostChange[] changes) =>
        Run(services => services.AddSingleton<IApplicationRepository>(repository), changes);

    [Fact]
    public async Task Every_changed_application_is_saved_with_the_planned_hosts_and_found_by_display_name_or_application_name()
    {
        var repository = new Applications(Site("siteA", "Site A", "site-a.example", isDefault: true), Site("Site_0B1C2D3E", "Site B", "site-b.example"));

        // By display name, and by application name in another case (the CLI sends the application name).
        var (result, _) = await Run(repository, Primary("Site A", "localhost:5001"), Primary("site_0b1c2d3e", "http://localhost:5002/"), Add("Site B", "www.site-b.example"));

        Assert.True(result!.Saved);
        Assert.Equal(["siteA", "Site_0B1C2D3E"], repository.SavedNames);
        var a = repository.Routable("siteA");
        // localhost gets http, what `opticli serve` listens with.
        Assert.Equal(
            [("localhost:5001", ApplicationHostType.Primary, UrlScheme.Http), ("site-a.example", ApplicationHostType.Default, UrlScheme.Https)],
            a.Hosts.Select(h => (h.Authority, h.Type, h.PreferredUrlScheme)).OrderBy(h => h.Authority));
        Assert.True(a.IsDefault);
        // Another host without a scheme gets the scheme of the application's URL before the change: https.
        Assert.Equal(UrlScheme.Https, repository.Routable("Site_0B1C2D3E").Hosts.Single(h => h.Authority == "www.site-b.example").PreferredUrlScheme);

        var viewA = result.Sites[0];
        Assert.Equal((null, "siteA", true, "Site A"), (viewA.Id, viewA.Application, viewA.IsDefault, viewA.Name));
        Assert.Equal("http://localhost:5001/", viewA.SiteUrl);
        Assert.DoesNotContain(viewA.Hosts, h => h.Name == "*");
        Assert.Contains("URL: https://site-a.example/ → http://localhost:5001/", viewA.Changes);
        Assert.DoesNotContain(viewA.Changes, c => c.StartsWith("SiteUrl", StringComparison.Ordinal));
        Assert.Equal((false, "http://localhost:5002/"), (result.Sites[1].IsDefault, result.Sites[1].SiteUrl));
        Assert.Contains(result.Warnings!, w => w.Contains("restart the site", StringComparison.Ordinal) && w.Contains("cached applications", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_star_host_moves_the_default_application_and_the_one_that_loses_it_is_saved_first()
    {
        var repository = new Applications(Site("siteA", "Site A", "site-a.example", isDefault: true), Site("siteB", "Site B", "site-b.example"));

        var (result, _) = await Run(repository, Remove("Site A", "*"), Add("Site B", "*"));

        Assert.Equal(["siteA", "siteB"], repository.SavedNames);
        Assert.Equal([("siteA", false), ("siteB", true)], repository.MadeDefault);
        Assert.False(repository.Routable("siteA").IsDefault);
        Assert.True(repository.Routable("siteB").IsDefault);
        Assert.Equal(["no longer the default application (*)"], result!.Sites[0].Changes);
        Assert.Equal(["made it the default application (*), which answers host names no application has"], result.Sites[1].Changes);
        Assert.Equal([false, true], result.Sites.Select(s => s.IsDefault!.Value));
        Assert.All(result.Sites, s => Assert.DoesNotContain(s.Hosts, h => h.Name == "*"));
        Assert.Contains(result.Warnings!, w => w.StartsWith("Site A is no longer the default application", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adding_the_star_host_moves_the_default_in_one_save_and_names_the_application_that_lost_it()
    {
        var repository = new Applications(Site("siteA", "Site A", "site-a.example", isDefault: true), Site("siteB", "Site B", "site-b.example"));

        var (result, _) = await Run(repository, Add("Site B", "*"));

        // One call: B's MakeDefaultAsync, which saves A without it (as the CMS's does); nothing saves A before that.
        Assert.Equal([("siteB", true)], repository.MadeDefault);
        Assert.Equal(["siteB", "siteA"], repository.SavedNames);
        Assert.Equal([("Site B", true), ("Site A", false)], result!.Sites.Select(s => (s.Name, s.IsDefault!.Value)));
        Assert.Equal(["no longer the default application (*): Site B is now"], result.Sites[1].Changes);
        Assert.Equal(SiteHostStatus.Changed, result.Sites[1].Status);
    }

    [Fact]
    public async Task When_the_new_default_is_saved_first_the_one_that_lost_it_is_saved_without_it()
    {
        var repository = new Applications(Site("siteA", "Site A", "site-a.example", isDefault: true), Site("siteB", "Site B", "site-b.example"));

        // B removes a host, so it is saved before A, whose copy was made while A was still the default.
        var (result, _) = await Run(repository, Add("Site B", "localhost:5002"), Remove("Site B", "site-b.example"), Add("Site B", "*"), Add("Site A", "localhost:5001"));

        Assert.Equal(["siteB", "siteA", "siteA"], repository.SavedNames);
        Assert.Equal([("siteB", true)], repository.MadeDefault);
        Assert.Equal((false, true), (repository.Routable("siteA").IsDefault, repository.Routable("siteB").IsDefault));
        Assert.Equal(["localhost:5001", "site-a.example"], repository.Routable("siteA").Hosts.Select(h => h.Authority).Order());
        Assert.Equal([("Site B", true), ("Site A", false)], result!.Sites.Select(s => (s.Name, s.IsDefault!.Value)));
    }

    [Fact]
    public async Task When_the_one_that_loses_the_default_is_saved_first_it_keeps_it_until_the_new_default_takes_it()
    {
        var repository = new Applications(Site("siteA", "Site A", "site-a.example", isDefault: true), Site("siteB", "Site B", "site-b.example"));

        // A removes a host, so it is saved first, still the default; B's MakeDefaultAsync then takes it.
        var (result, _) = await Run(repository, Add("Site A", "localhost:5001"), Remove("Site A", "site-a.example"), Add("Site B", "*"));

        Assert.Equal(["siteA", "siteB", "siteA"], repository.SavedNames);
        Assert.Equal([("siteB", true)], repository.MadeDefault);
        Assert.Equal((false, true), (repository.Routable("siteA").IsDefault, repository.Routable("siteB").IsDefault));
        Assert.Equal(["localhost:5001"], repository.Routable("siteA").Hosts.Select(h => h.Authority));
        Assert.Equal([false, true], result!.Sites.OrderBy(s => s.Name).Select(s => s.IsDefault!.Value));
    }

    [Fact]
    public async Task An_application_the_cms_refuses_after_another_was_saved_names_every_error_and_what_was_saved()
    {
        // As the CMS throws it (ValidationServiceExtensions.ThrowException): the first error as the message, errors by
        // property name in Data (one each, or a list), and none of those without a property name; its validation has all.
        var refusal = new ValidationException(new ValidationResult("Only one primary host is allowed per locale", ["Hosts", "Hosts", "Hosts[0].Authority", ""]), null, null);
        refusal.Data["Hosts"] = new List<ValidationError>
        {
            new() { ErrorMessage = "Only one primary host is allowed per locale", PropertyName = "Hosts" },
            new() { ErrorMessage = "Only one Edit host is allowed", PropertyName = "Hosts" },
        };
        refusal.Data["Hosts[0].Authority"] = new ValidationError { ErrorMessage = "Invalid host name 'x'", PropertyName = "Hosts[0].Authority" };
        var validation = new Validation(new ValidationError { ErrorMessage = "Application type 'X' is not supported." }, new ValidationError { ErrorMessage = "Only one Edit host is allowed", PropertyName = "Hosts" });
        var repository = new Applications(Site("siteA", "Site A", "site-a.example"), Site("siteB", "Site B", "site-b.example")) { Refuse = ("siteB", refusal) };

        var (_, error) = await Run(services => services.AddSingleton<IApplicationRepository>(repository).AddSingleton<IValidationService>(validation), [Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5002")]);

        Assert.Equal(AgentErrorCodes.Validation, error!.Code);
        Assert.Contains("Site B: the CMS refused the site's hosts: Only one primary host is allowed per locale Only one Edit host is allowed Invalid host name 'x' Application type 'X' is not supported.", error.Message);
        Assert.EndsWith("Already saved: Site A; not saved: Site B.", error.Message);
        Assert.Equal(["siteA"], repository.SavedNames);
    }

    [Fact]
    public async Task Without_the_cms_validation_the_errors_in_the_exception_are_all_there_is()
    {
        var refusal = new ValidationException(new ValidationResult("Only one primary host is allowed per locale", ["Hosts"]), null, null);
        refusal.Data["Hosts"] = new List<ValidationError> { new() { ErrorMessage = "Only one primary host is allowed per locale" }, new() { ErrorMessage = "Only one Edit host is allowed" } };
        var repository = new Applications(Site("siteA", "Site A", "site-a.example")) { Refuse = ("siteA", refusal) };

        var (_, error) = await Run(services => services.AddSingleton<IApplicationRepository>(repository).AddSingleton<IValidationService>(new Validation(failing: true)), [Primary("Site A", "localhost:5001")]);

        Assert.Contains("the CMS refused the site's hosts: Only one primary host is allowed per locale Only one Edit host is allowed", error!.Message);
    }

    [Fact]
    public async Task An_application_deleted_meanwhile_stops_the_batch_before_any_is_saved()
    {
        var repository = new Applications(Site("siteA", "Site A", "site-a.example"), Site("siteB", "Site B", "site-b.example")) { Vanished = "siteB" };

        var (_, error) = await Run(repository, Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5002"));

        Assert.Equal(AgentErrorCodes.NotFound, error!.Code);
        Assert.Contains("nothing was saved", error.Message);
        Assert.Empty(repository.SavedNames);
    }

    [Fact]
    public async Task A_dry_run_shows_the_hosts_by_name_and_the_url_the_cms_would_make_without_saving()
    {
        var repository = new Applications(Site("siteA", "Site A", "site-a.example"));

        var (result, _) = await Run(services => services.AddSingleton<IApplicationRepository>(repository), [Primary("siteA", "localhost:5001"), Add("siteA", "a.localhost")], dryRun: true);

        Assert.Empty(repository.SavedNames);
        Assert.False(result!.Saved);
        var site = Assert.Single(result.Sites);
        Assert.Equal(["a.localhost", "localhost:5001", "site-a.example"], site.Hosts.Select(h => h.Name));
        Assert.Equal(new SiteHost("a.localhost", HostTypes.Undefined, null, false), site.Hosts[0]);
        Assert.Equal("http://localhost:5001/", site.SiteUrl);
    }

    /// <summary>The repository's contract as the operation uses it: List, Get, SaveAsync, MakeDefaultAsync.</summary>
    private sealed class Applications(params InProcessWebsite[] applications) : IApplicationRepository
    {
        private readonly List<Application> _applications = [.. applications];

        public (string Name, Exception Failure)? Refuse { get; init; }

        /// <summary>Listed, but gone by the time the operation loads it to save.</summary>
        public string? Vanished { get; init; }

        public List<string> SavedNames { get; } = [];

        public List<(string Name, bool Enable)> MadeDefault { get; } = [];

        public IRoutableApplication Routable(string name) => (IRoutableApplication)_applications.Single(a => a.Name == name);

        /// <summary><c>IsDefault</c>'s setter is internal: only the CMS's repository sets it.</summary>
        public static void SetDefault(Application application, bool value) =>
            application.GetType().GetProperty(nameof(InProcessWebsite.IsDefault))!.SetValue(application, value);

        public IEnumerable<Application> List() => _applications;

        public Task<IEnumerable<Application>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(List());

        public Application? Get(string name) => name == Vanished ? null : _applications.SingleOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public Task<Application?> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Get(name));

        public Task SaveAsync(Application application, CancellationToken cancellationToken = default)
        {
            if (Refuse is { } refuse && refuse.Name == application.Name)
            {
                throw refuse.Failure;
            }
            _applications[_applications.FindIndex(a => a.Name == application.Name)] = application;
            SavedNames.Add(application.Name);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>As the CMS's: saves the application with it, then the one that was the default without it.</summary>
        public async Task MakeDefaultAsync(IRoutableApplication application, bool enable, CancellationToken cancellationToken = default)
        {
            var saved = (Application)application;
            MadeDefault.Add((saved.Name, enable));
            var current = enable ? _applications.FirstOrDefault(a => a.Name != saved.Name && ((IRoutableApplication)a).IsDefault) : null;
            SetDefault(saved, enable);
            await SaveAsync(saved, cancellationToken);
            if (current?.CreateWritableClone() is { } previous)
            {
                SetDefault(previous, false);
                await SaveAsync(previous, cancellationToken);
            }
        }

        public Task EnableAssetsAsync(IResourceableApplication application, bool enable, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>The CMS's validation service, run again for the errors a <see cref="ValidationException"/> leaves out.</summary>
    private sealed class Validation(params ValidationError[] errors) : IValidationService
    {
        public Validation(bool failing) : this() => Failing = failing;

        private bool Failing { get; }

        public IEnumerable<ValidationError> Validate(object instance) => Failing ? throw new InvalidOperationException("A validator broke.") : errors;
    }
#endif

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
