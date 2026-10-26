using EPiServer.Azure.Blobs;
using EPiServer.Azure.Events;
using EPiServer.Events;
using EPiServer.Events.Providers;
using EPiServer.Framework.Blobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OptiCli.Agent.Hosting;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Hosting;

/// <summary>
/// A sealed apply's production event bus and blob storage in the site (<see cref="SealedAzure"/>): the CMS's Azure providers,
/// reached by reflection, registered with what the user typed whatever the site configures, read back for the run, and the
/// event queue flushed before the site stops. The CMS's Azure packages can't be referenced next to the oldest CMS the agent
/// builds against, so stand-ins with the same type and member names (as in EPiServer.Azure 11.2.2 and
/// EPiServer.Events.Azure 13.4.0) are found in this assembly instead.
/// </summary>
[Collection(HostingCollection.Name)]
public sealed class SealedAzureTests : IDisposable
{
    private const string Bus = "Endpoint=sb://prod-bus.servicebus.windows.net/;SharedAccessKeyName=manage;SharedAccessKey=bus-secret";
    private const string Storage = "DefaultEndpointsProtocol=https;AccountName=prodmedia;AccountKey=YmxvYi1zZWNyZXQ=";

    public SealedAzureTests()
    {
        var here = typeof(SealedAzureTests).Assembly.GetName().Name!;
        SealedAzure.EventAssemblies = ["EPiServer.Events.Azure.NotThere", here];
        SealedAzure.BlobAssemblies = [here];
        SealedAzure.RaiseDelay = TimeSpan.Zero;
    }

    public void Dispose()
    {
        SealedAzure.EventAssemblies = SealedAzure.DefaultEventAssemblies;
        SealedAzure.BlobAssemblies = SealedAzure.DefaultBlobAssemblies;
        SealedAzure.RaiseDelay = TimeSpan.FromSeconds(1);
    }

    private static AgentSettings Sealed(bool events = true, bool blobs = true) =>
        Settings(pinned: Local) with
        {
            Events = events ? new AzureEvents(Bus, "SiteEvents") : null,
            Blobs = blobs ? new AzureBlobs(Storage, "media") : null,
        };

    [Fact]
    public void The_providers_are_registered_with_what_was_typed_over_the_sites_own_settings()
    {
        using var host = Build(Sealed(), site: services =>
        {
            // What a site's Development setup might do, after the hosting startup.
            services.Configure<AzureEventProviderOptions>(o => { o.ConnectionString = "Endpoint=sb://staging-bus.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=other"; o.TopicName = "StagingEvents"; });
            services.Configure<AzureBlobProviderOptions>(o => o.ContainerName = "staging-media");
            services.Configure<BlobProvidersOptions>(o => o.DefaultProvider = "default");
        }, keyVault: new() { ["ConnectionStrings:EPiServerAzureEvents"] = "Endpoint=sb://staging-bus.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=other" });

        var status = SealedAzure.Status(host.Services, Sealed());
        var configuration = host.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(new SealedEvents(SealedAzure.EventProviderType, "SiteEvents", "DEVBOX_1a2b3c4d", Connected: false, ConnectionMatches: true), status.Events);
        Assert.Equal(new SealedBlobs("azure", SealedAzure.BlobProviderType, "media", ConnectionMatches: true), status.Blobs);
        Assert.Null(host.Services.GetRequiredService<IOptions<AzureEventProviderOptions>>().Value.Endpoint);
        Assert.Equal(Bus, configuration.GetConnectionString("EPiServerAzureEvents"));
        Assert.Equal(Storage, configuration.GetConnectionString("EPiServerAzureBlobs"));
    }

    [Fact]
    public void An_endpoint_the_site_sets_after_the_agent_takes_the_connections_place()
    {
        using var host = Build(Sealed(), after: services =>
        {
            services.PostConfigure<AzureEventProviderOptions>(o => o.Endpoint = "other-bus.servicebus.windows.net");
            services.PostConfigure<AzureBlobProviderOptions>(o => o.ServiceUri = new Uri("https://othermedia.blob.core.windows.net"));
        });

        var status = SealedAzure.Status(host.Services, Sealed());

        Assert.False(status.Events!.ConnectionMatches);
        Assert.False(status.Blobs!.ConnectionMatches);
    }

    [Fact]
    public void The_log_says_which_without_the_connection_strings()
    {
        string? log = null;
        using var host = Build(Sealed(), log: text => log = text);

        Assert.Contains("production's event bus for this run, topic 'SiteEvents'", log, StringComparison.Ordinal);
        Assert.Contains("production's blob storage for this run, container 'media'", log, StringComparison.Ordinal);
        Assert.DoesNotContain("bus-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("YmxvYi1zZWNyZXQ", log, StringComparison.Ordinal);
        Assert.DoesNotContain("bus-secret", new AzureEvents(Bus, "SiteEvents").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_them_nothing_is_registered()
    {
        using var host = Build(Sealed(events: false, blobs: false));

        Assert.Null(host.Services.GetService<EventProvider>());
        Assert.Equal(new SealedStatus(null, null), SealedAzure.Status(host.Services, Sealed(events: false, blobs: false)));
    }

    [Fact]
    public void A_site_without_the_cms_azure_providers_doesnt_start()
    {
        SealedAzure.EventAssemblies = ["EPiServer.Events.Azure.NotThere"];

        var ex = Assert.Throws<InvalidOperationException>(() => Build(Sealed(blobs: false)));

        Assert.StartsWith("[opticli] Refusing to start: production's event bus was given", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_event_provider_in_effect_is_reported()
    {
        using var host = Build(Sealed(blobs: false), site: services => services.AddSingleton<EventProvider, OtherEventProvider>());

        var events = SealedAzure.Status(host.Services, Sealed(blobs: false)).Events!;

        Assert.Equal(typeof(OtherEventProvider).FullName, events.Provider);
        Assert.False(events.Connected);
        Assert.NotNull(events.Problem);
    }

    [Fact]
    public async Task The_queue_is_sent_once_connected_and_completed_by_a_flush()
    {
        using var host = Build(Sealed(blobs: false));
        var provider = (AzureEventProvider)host.Services.GetRequiredService<EventProvider>();

        var early = await SealedAzure.FlushAsync(host.Services, CancellationToken.None);
        provider.ServiceBus.Connect();
        var connected = SealedAzure.Status(host.Services, Sealed(blobs: false)).Events!.Connected;
        var flushed = await SealedAzure.FlushAsync(host.Services, CancellationToken.None);

        Assert.False(early.Flushed);
        Assert.Contains("never connected", early.Problem, StringComparison.Ordinal);
        Assert.True(connected);
        Assert.True(flushed.Flushed);
        Assert.True(provider.ServiceBus.Completed);
    }

    [Fact]
    public void Cms_12_keeps_the_service_bus_client_in_a_field()
    {
        var inner = SealedAzure.Inner(new Cms12Provider());

        Assert.IsType<DefaultServiceBusEventProvider>(inner);
        Assert.False(SealedAzure.Connected(inner!));
    }

    [Fact]
    public void Only_values_from_the_runs_stdin_line_count_never_the_environment()
    {
        var names = new[] { AgentProtocol.SecretsVariable, AgentProtocol.EventsConnectionVariable, AgentProtocol.EventsTopicVariable, AgentProtocol.BlobsConnectionVariable, AgentProtocol.BlobsContainerVariable };
        var before = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            // Exported in the shell, not given at the terminal.
            Environment.SetEnvironmentVariable(AgentProtocol.EventsConnectionVariable, Bus);
            Environment.SetEnvironmentVariable(AgentProtocol.EventsTopicVariable, "SiteEvents");
            Environment.SetEnvironmentVariable(AgentProtocol.SecretsVariable, AgentProtocol.SecretsFromStdin);
            StdinSecrets.Read(new StringReader($$"""{"OPTICLI_TOKEN": "t0k", "OPTICLI_DB": "{{Local}}", "{{AgentProtocol.BlobsConnectionVariable}}": "{{Storage}}", "{{AgentProtocol.BlobsContainerVariable}}": "media"}"""));
            Environment.SetEnvironmentVariable(AgentProtocol.EventsConnectionVariable, Bus);
            Environment.SetEnvironmentVariable(AgentProtocol.EventsTopicVariable, "SiteEvents");

            var settings = AgentSettings.FromEnvironment(_ => null);

            Assert.Null(settings.Events);
            Assert.Equal(new AzureBlobs(Storage, "media"), settings.Blobs);
            Environment.SetEnvironmentVariable(AgentProtocol.SecretsVariable, null);
            Assert.Null(AgentSettings.FromEnvironment(_ => null).Blobs);
        }
        finally
        {
            StdinSecrets.Forget();
            foreach (var (name, value) in before)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <param name="after">What the site registers after the agent's hosting startup (its own PostConfigure, say).</param>
    private static IHost Build(AgentSettings settings, Action<IServiceCollection>? site = null, Dictionary<string, string?>? keyVault = null, Action<string>? log = null, Action<IServiceCollection>? after = null)
    {
        var builder = new HostBuilder();
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:EPiServerDB"] = Local });
            config.AddInMemoryCollection(keyVault ?? []);
        });
        builder.ConfigureWebHost(web =>
        {
            AgentHostingStartup.Configure(web, settings);
            web.ConfigureServices(services => site?.Invoke(services));
        });
        builder.ConfigureServices(services => after?.Invoke(services));
        IHost? host = null;
        var stderr = Stderr(() => host = builder.Build());
        log?.Invoke(stderr);
        return host!;
    }

    /// <summary>CMS 12's shape: the client in a private field <c>_provider</c>.</summary>
    private sealed class Cms12Provider : EventProvider
    {
#pragma warning disable CS0414, IDE0052 // Read by reflection.
        private readonly DefaultServiceBusEventProvider _provider = new();
#pragma warning restore CS0414, IDE0052

        public override void SendMessage(EventMessage message)
        {
        }
    }

    private sealed class OtherEventProvider : EventProvider
    {
        public override void SendMessage(EventMessage message)
        {
        }
    }
}

/// <summary>Stand-ins for the CMS's <c>AddAzureEventProvider</c> and <c>AddAzureBlobProvider</c>.</summary>
public static class AzureServiceCollectionExtensions
{
    public static IServiceCollection AddAzureEventProvider(this IServiceCollection services, Action<AzureEventProviderOptions>? configureOptions = null)
    {
        services.AddSingleton<EventProvider>(new AzureEventProvider());
        services.Configure<AzureEventProviderOptions>(o => o.TopicName ??= "MySiteEvents");
        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        return services;
    }

    public static IServiceCollection AddAzureBlobProvider(this IServiceCollection services, Action<AzureBlobProviderOptions>? configureOptions = null)
    {
        services.Configure<BlobProvidersOptions>(o =>
        {
            o.DefaultProvider = "azure";
            o.AddProvider("azure", typeof(AzureBlobProvider).AssemblyQualifiedName!);
        });
        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        return services;
    }
}
