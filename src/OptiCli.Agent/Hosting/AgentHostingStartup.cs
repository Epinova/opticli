using EPiServer.Data;
using EPiServer.DataAbstraction.RuntimeModel.Internal;
using EPiServer.Scheduler;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
/// (jobs would run alongside the deployed site's), automatic schema updates (a newer CMS package in the local build)
/// and the commit phase of content type sync (the local branch's models) are turned off.
/// </para>
/// </remarks>
public sealed class AgentHostingStartup : IHostingStartup
{
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
                Console.Error.WriteLine("[opticli] Shared database: scheduler, automatic schema updates and content type sync are off for this run.");
            }
            services.AddSingleton<IValidateOptions<DataAccessOptions>, DataAccessOptionsGuard>();
            services.AddTransient<IStartupFilter, AgentStartupFilter>();
        });
    }
}
