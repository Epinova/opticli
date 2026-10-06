using EPiServer.Data;
using EPiServer.Data.Dynamic;
// ContentModelOptions: in RuntimeModel.Internal on CMS 12, in RuntimeModel on CMS 13.
using EPiServer.DataAbstraction.RuntimeModel;
using EPiServer.DataAbstraction.RuntimeModel.Internal;
using EPiServer.Scheduler;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiCli.Agent.Drift;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Safety;

[assembly: HostingStartup(typeof(AgentHostingStartup))]

namespace OptiCli.Agent.Hosting;

/// <summary>
/// Pins the site to the CLI's database and refuses to start it against anything that isn't local or the one remote
/// development database the CLI approved.
/// </summary>
/// <remarks>
/// The pin is applied twice, because the CMS doesn't necessarily read <c>ConnectionStrings</c>:
/// <list type="number">
/// <item>As the last configuration source, for code that reads <c>ConnectionStrings:&lt;name&gt;</c>.
/// With the generic host, hosting startups run inside ConfigureWebHostDefaults, after the site's own
/// ConfigureAppConfiguration, so this beats user secrets and Key Vault.</item>
/// <item>As a PostConfigure of the CMS's <see cref="DataAccessOptions"/>, which is what its database
/// layer actually connects with and which a site may also set from <c>Cms:DataAccess</c> or code.</item>
/// </list>
/// The guard likewise runs twice: on the configuration when services are registered (fail fast), and
/// as options validation on the final <see cref="DataAccessOptions"/> (catches anything that changed
/// it later, e.g. configuration sources a minimal-hosting site adds after the builder is created).
/// <para>
/// Against a remote database (shared mode) the site must not change it for others just by starting: the scheduler
/// (jobs would run alongside the deployed site's), automatic schema updates (a newer CMS package in the local build),
/// the commit phase of content type sync (the local branch's models) and the remapping of Dynamic Data Store types
/// whose properties changed are turned off. What the local code would have changed is reported as drift instead
/// (<see cref="DriftCheck"/>).
/// </para>
/// <para>
/// The scheduler is off against a local database too, unless the CLI asks to leave it (<c>serve --scheduler</c>,
/// <see cref="AgentSettings.Scheduler"/>): a restored production database has jobs that are overdue, and the site would
/// start every one of them at once (imports, emails, emptying the recycle bin). The site still registers its jobs, and
/// <c>opticli jobs run</c> still starts one. A site that turns the scheduler on in its own <c>PostConfigure</c> wins,
/// which <c>ping</c> reports.
/// </para>
/// </remarks>
public sealed class AgentHostingStartup : IHostingStartup
{
    /// <summary>What the site's output says when the agent turned its scheduler off (every run but <c>serve --scheduler</c>).</summary>
    internal const string SchedulerOffLine = "[opticli] Scheduler is off for this run (jobs still run with \"opticli jobs run\"; --scheduler turns it on).";

    public void Configure(IWebHostBuilder builder)
    {
        HostingStartupCheck.MarkConfigured();
        Configure(builder, AgentSettings.FromEnvironment());
    }

    internal static void Configure(IWebHostBuilder builder, AgentSettings settings)
    {
        if (settings.PinnedConnection is { } pinned)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{settings.ConnectionName}"] = pinned,
            }));
        }

        builder.ConfigureServices((context, services) =>
        {
            var effective = context.Configuration.GetConnectionString(settings.ConnectionName);
            DatabasePin.EnsureAllowed(settings, settings.ConnectionName, effective);
            DatabasePin.WarnAboutOtherRemoteStrings(settings, context.Configuration, settings.ConnectionName);

            if (settings.SharedDatabase && Compat.AgentBuild.CmsMajor >= 13)
            {
                // The CLI refuses this before starting; this is for a site started with an older `opticli env`'s variables.
                throw new InvalidOperationException("[opticli] Refusing to start: this opticli doesn't run CMS 13 sites against a shared database yet (it can't check what the site would change there). Use a local copy of the database.");
            }
            services.AddSingleton(settings);
            if (settings.PinnedConnection is not null)
            {
                services.PostConfigure<DataAccessOptions>(options => options.SetConnectionString(settings.PinnedConnection));
            }
            if (settings.SharedDatabase)
            {
                services.PostConfigure<DataAccessOptions>(options =>
                {
                    options.UpdateDatabaseSchema = false;
                    options.CreateDatabaseSchema = false;
                });
                services.PostConfigure<SchedulerOptions>(options => options.Enabled = false);
                services.PostConfigure<ContentModelOptions>(options => options.EnableModelSyncCommit = false);
                services.PostConfigure<DynamicDataStoreOptions>(TurnOffStoreChanges);
                Console.Error.WriteLine("[opticli] Shared database: scheduler, automatic schema updates, content type sync and store remapping are off for this run.");
            }
            else if (settings.SchedulerOff)
            {
                services.PostConfigure<SchedulerOptions>(options => options.Enabled = false);
                Console.Error.WriteLine(SchedulerOffLine);
            }
            services.AddSingleton<IValidateOptions<DataAccessOptions>, DataAccessOptionsGuard>();
            services.AddSingleton<DriftCheck>();
            services.AddTransient<IStartupFilter, AgentStartupFilter>();
        });
    }

    /// <summary>
    /// A store whose type changed is then not remapped (or upgraded) in the database: using it fails instead, and drift
    /// reports it. <c>SeamlessUpgradeStores</c> came after CMS 12.0, so it is set by name where it exists.
    /// </summary>
    internal static void TurnOffStoreChanges(DynamicDataStoreOptions options)
    {
        options.AutoRemapStores = false;
        if (typeof(DynamicDataStoreOptions).GetProperty("SeamlessUpgradeStores") is { CanWrite: true, PropertyType: var type } upgrade && type == typeof(bool))
        {
            upgrade.SetValue(options, false);
        }
    }
}
