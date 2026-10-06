using System.Reflection;
using EPiServer.Data;
using EPiServer.Data.Dynamic;
// ContentModelOptions: RuntimeModel.Internal on CMS 12, RuntimeModel on CMS 13.
using EPiServer.DataAbstraction.RuntimeModel;
using EPiServer.DataAbstraction.RuntimeModel.Internal;
using EPiServer.Scheduler;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OptiCli.Agent.Hosting;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Hosting;

/// <summary>
/// The hosting startup on a generic host built the way a CMS 12 site builds it: the site's own configuration first,
/// then ConfigureWebHostDefaults, inside which ASP.NET Core runs hosting startups. The host is built, never started.
/// </summary>
[Collection(HostingCollection.Name)]
public class AgentHostingStartupTests
{
    [Fact]
    public void The_agent_assembly_declares_its_hosting_startup()
    {
        var attribute = typeof(AgentHostingStartup).Assembly.GetCustomAttribute<HostingStartupAttribute>();

        Assert.Equal(typeof(AgentHostingStartup), attribute?.HostingStartupType);
    }

    [Fact]
    public void The_pin_is_the_last_configuration_source_so_it_beats_the_sites_own()
    {
        using var host = Build(Settings(pinned: Local), site: config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // As user secrets or Key Vault would, ahead of the hosting startup.
            ["ConnectionStrings:EPiServerDB"] = OtherLocal,
        }));

        Assert.Equal(Local, host.Services.GetRequiredService<IConfiguration>().GetConnectionString("EPiServerDB"));
        Assert.Equal(Local, DatabasePin.Resolve(host.Services.GetRequiredService<IOptions<DataAccessOptions>>().Value)?.ConnectionString);
    }

    [Fact]
    public void A_source_added_after_the_hosting_startup_that_moves_the_connection_stops_the_site()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Settings(pinned: Local), after: host => host.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:EPiServerDB"] = OtherLocal }))));

        Assert.Contains("overrode the pinned connection string 'EPiServerDB'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_pin_a_remote_connection_string_stops_the_site()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Settings(), site: config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EPiServerDB"] = Remote,
        })));

        Assert.StartsWith("[opticli] Refusing to start: connection string 'EPiServerDB' is not local.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Site_code_that_sets_the_cms_connection_is_overridden_by_the_pin()
    {
        using var host = Build(Settings(pinned: Local), after: builder => builder.ConfigureServices(services =>
            services.Configure<DataAccessOptions>(options => options.SetConnectionString(OtherLocal))));

        var options = host.Services.GetRequiredService<IOptions<DataAccessOptions>>().Value;

        Assert.Equal(Local, DatabasePin.Resolve(options)?.ConnectionString);
    }

    [Fact]
    public void A_later_change_to_the_cms_connection_fails_options_validation()
    {
        using var host = Build(Settings(pinned: Local), after: builder => builder.ConfigureServices(services =>
            services.PostConfigure<DataAccessOptions>(options => options.SetConnectionString(OtherRemote))));

        var ex = Assert.Throws<OptionsValidationException>(() => Stderr(() => _ = host.Services.GetRequiredService<IOptions<DataAccessOptions>>().Value));

        Assert.Contains(ex.Failures, f => f.StartsWith("[opticli] CMS connection 'EPiServerDB' is not local.", StringComparison.Ordinal));
        Assert.Contains("[opticli] The CMS would connect with 'EPiServerDB', which is not the pinned connection string.", ex.Failures);
    }

#if CMS13
    [Fact]
    public void A_shared_database_is_refused_on_cms_13_for_now()
    {
        // The CLI refuses first; this is the backstop for a site started with an older `opticli env`'s variables.
        var ex = Assert.Throws<InvalidOperationException>(() => Stderr(() => Build(Settings(pinned: Remote, approvedRemote: RemoteApproval)).Dispose()));

        Assert.StartsWith("[opticli] Refusing to start: this opticli doesn't run CMS 13 sites against a shared database yet", ex.Message, StringComparison.Ordinal);
    }
#else
    [Fact]
    public void A_shared_database_turns_off_what_would_change_it_for_others()
    {
        string? stderr = null;
        using var host = Build(Settings(pinned: Remote, approvedRemote: RemoteApproval), log: s => stderr = s);

        var data = host.Services.GetRequiredService<IOptions<DataAccessOptions>>().Value;
        Assert.False(data.UpdateDatabaseSchema);
        Assert.False(data.CreateDatabaseSchema);
        Assert.Equal(Remote, DatabasePin.Resolve(data)?.ConnectionString);
        Assert.False(host.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value.Enabled);
        Assert.False(host.Services.GetRequiredService<IOptions<ContentModelOptions>>().Value.EnableModelSyncCommit);
        Assert.False(host.Services.GetRequiredService<IOptions<DynamicDataStoreOptions>>().Value.AutoRemapStores);
        Assert.Contains("Shared database: scheduler, automatic schema updates, content type sync and store remapping are off", stderr, StringComparison.Ordinal);
    }
#endif

    [Fact]
    public void The_scheduler_is_off_for_every_run_unless_serve_asks_to_leave_it()
    {
        string? stderr = null;
        using var off = Build(Settings(pinned: Local), log: s => stderr = s, after: builder => builder.ConfigureServices(services =>
            services.Configure<SchedulerOptions>(options => options.Enabled = true)));
        using var on = Build(Settings(pinned: Local, scheduler: true), after: builder => builder.ConfigureServices(services =>
            services.Configure<SchedulerOptions>(options => options.Enabled = true)));
        using var siteOff = Build(Settings(pinned: Local, scheduler: true), after: builder => builder.ConfigureServices(services =>
            services.Configure<SchedulerOptions>(options => options.Enabled = false)));

        Assert.False(off.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value.Enabled);
        Assert.Contains(AgentHostingStartup.SchedulerOffLine, stderr, StringComparison.Ordinal);
        Assert.True(on.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value.Enabled);
        Assert.False(siteOff.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value.Enabled);
    }

#if !CMS13
    [Fact]
    public void A_shared_database_keeps_the_scheduler_off_even_when_serve_asks_to_leave_it()
    {
        using var host = Build(Settings(pinned: Remote, approvedRemote: RemoteApproval, scheduler: true));

        Assert.False(host.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value.Enabled);
    }
#endif

    [Fact]
    public void A_local_database_keeps_the_sites_own_settings()
    {
        using var host = Build(Settings(pinned: Local, scheduler: true), after: builder => builder.ConfigureServices(services =>
            services.Configure<DataAccessOptions>(options => options.UpdateDatabaseSchema = false)));

        var data = host.Services.GetRequiredService<IOptions<DataAccessOptions>>().Value;
        Assert.False(data.UpdateDatabaseSchema);
        Assert.True(data.CreateDatabaseSchema);
        Assert.Equal(new SchedulerOptions().Enabled, host.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value.Enabled);
        Assert.Equal(new ContentModelOptions().EnableModelSyncCommit, host.Services.GetRequiredService<IOptions<ContentModelOptions>>().Value.EnableModelSyncCommit);
        Assert.True(host.Services.GetRequiredService<IOptions<DynamicDataStoreOptions>>().Value.AutoRemapStores);
    }

    [Fact]
    public void The_settings_guard_and_startup_filter_are_registered()
    {
        using var host = Build(Settings(pinned: Local));

        Assert.Equal(Local, host.Services.GetRequiredService<AgentSettings>().PinnedConnection);
        Assert.Contains(host.Services.GetServices<IValidateOptions<DataAccessOptions>>(), v => v is DataAccessOptionsGuard);
        Assert.Contains(host.Services.GetServices<IStartupFilter>(), f => f is AgentStartupFilter);
        Assert.NotNull(host.Services.GetRequiredService<OptiCli.Agent.Drift.DriftCheck>());
    }

    [Fact]
    public void Store_changes_are_turned_off_on_every_cms_12_version()
    {
        // SeamlessUpgradeStores only exists in later CMS 12 versions; on 12.0 there is nothing more to turn off.
        var options = new DynamicDataStoreOptions();

        AgentHostingStartup.TurnOffStoreChanges(options);

        Assert.False(options.AutoRemapStores);
        Assert.True(options.AutoResolveTypes);
    }

    /// <param name="site">The site's own configuration (appsettings, user secrets, Key Vault), added before the web host.</param>
    /// <param name="after">What the site configures after ConfigureWebHostDefaults.</param>
    private static IHost Build(AgentSettings settings, Action<IConfigurationBuilder>? site = null, Action<IHostBuilder>? after = null, Action<string>? log = null)
    {
        var builder = new HostBuilder();
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:EPiServerDB"] = Local });
            site?.Invoke(config);
        });
        builder.ConfigureWebHost(web => AgentHostingStartup.Configure(web, settings));
        after?.Invoke(builder);

        IHost? host = null;
        var stderr = Stderr(() => host = builder.Build());
        log?.Invoke(stderr);
        return host!;
    }
}
