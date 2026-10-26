using System.Reflection;
using EPiServer.Events.Providers;
using EPiServer.Framework.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiCli.Protocol;

namespace OptiCli.Agent.Hosting;

/// <summary>A sealed apply's production event bus, as the user typed it. The connection string is never printed.</summary>
internal sealed record AzureEvents(string ConnectionString, string Topic)
{
    public override string ToString() => $"topic '{Topic}' (connection string not shown)";
}

/// <summary>A sealed apply's production blob storage, as the user typed it. The connection string is never printed.</summary>
internal sealed record AzureBlobs(string ConnectionString, string Container)
{
    public override string ToString() => $"container '{Container}' (connection string not shown)";
}

/// <summary>
/// Production's event bus and blob storage for a sealed apply's site: the CMS's own Azure providers, registered with what
/// the user typed at the run's terminal (<see cref="AgentSettings.Events"/>, <see cref="AgentSettings.Blobs"/>), whatever the
/// site's configuration says (its Key Vault may hold another environment's). opticli doesn't reference the Azure packages:
/// they are reached by reflection in the site's own assemblies, so a site that doesn't use Azure loads nothing new, and the
/// CLI only offers this when the build has them.
/// </summary>
/// <remarks>
/// CMS 12 has both providers in <c>EPiServer.Azure</c> (<c>AzureServiceCollectionExtensions</c>), CMS 13 in
/// <c>EPiServer.Events.Azure</c> and <c>EPiServer.Blobs.Azure</c> (<c>EPiServer.DependencyInjection</c>); the option types have
/// the same names in both. The event provider keeps the Service Bus client in an inner <c>DefaultServiceBusEventProvider</c>
/// (CMS 12: field <c>_provider</c>, CMS 13: property <c>ServiceBus</c>) whose <c>_sendProcessor</c> is a buffer until it has
/// connected, then a sending block: found by type name where it can be. Checked against EPiServer.Azure 11.2.2 and
/// EPiServer.Events.Azure 13.4.0. What isn't found leaves the run without events (the CLI then stops before writing, or
/// says a restart is needed), never the other way round.
/// </remarks>
internal static class SealedAzure
{
    internal const string EventOptionsType = "EPiServer.Azure.Events.AzureEventProviderOptions";

    internal const string BlobOptionsType = "EPiServer.Azure.Blobs.AzureBlobProviderOptions";

    internal const string EventProviderType = "EPiServer.Azure.Events.AzureEventProvider";

    internal const string BlobProviderType = "EPiServer.Azure.Blobs.AzureBlobProvider";

    /// <summary>The name the CMS registers its Azure blob provider under.</summary>
    internal const string BlobProviderName = "azure";

    /// <summary>Where the providers are, newest CMS first.</summary>
    internal static readonly IReadOnlyList<string> DefaultEventAssemblies = ["EPiServer.Events.Azure", "EPiServer.Azure"];

    internal static readonly IReadOnlyList<string> DefaultBlobAssemblies = ["EPiServer.Blobs.Azure", "EPiServer.Azure"];

    /// <summary>Where the event provider is looked for (tests point it at stand-ins).</summary>
    internal static IReadOnlyList<string> EventAssemblies { get; set; } = DefaultEventAssemblies;

    internal static IReadOnlyList<string> BlobAssemblies { get; set; } = DefaultBlobAssemblies;

    /// <summary>How long a flush waits for the provider's queue to be sent.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the site must have raised no cache removal before a flush sends the queue: a save's cache removals keep coming
    /// for a few seconds after it (the site's own publish handlers save more), and the CMS raises them without waiting
    /// (<c>RemoteCacheSynchronization.RemoveRemote</c>). Seen on a real site: six of them 1 to 2 s after a fixed 1 s wait.
    /// </summary>
    internal static TimeSpan RaiseQuiet { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a flush waits for the site to go quiet at most.</summary>
    internal static TimeSpan RaiseQuietMax { get; set; } = TimeSpan.FromSeconds(60);


    /// <summary>The CMS's cache removal event (<c>RemoteCacheSynchronization.RemoveFromCacheEventId</c>), the same on CMS 12 and 13.</summary>
    private static readonly Guid RemoveFromCacheEventId = new("9484e34b-b419-4e59-8fd5-3277668a7fce");

    /// <summary>The connection strings, as the last configuration source: for code in the site that reads them itself.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Configuration(AgentSettings settings)
    {
        if (settings.Events is { } events)
        {
            yield return new("ConnectionStrings:EPiServerAzureEvents", events.ConnectionString);
        }
        if (settings.Blobs is { } blobs)
        {
            yield return new("ConnectionStrings:EPiServerAzureBlobs", blobs.ConnectionString);
        }
    }

    /// <summary>Registers the providers the run was given, with its values winning over the site's.</summary>
    /// <exception cref="InvalidOperationException">The site has no such provider: it must not start (the CLI checks the build first).</exception>
    public static void Register(IServiceCollection services, AgentSettings settings)
    {
        if (settings.Events is { } events)
        {
            var (add, options) = Find("AddAzureEventProvider", EventAssemblies)
                ?? throw new InvalidOperationException($"[opticli] Refusing to start: production's event bus was given for this run, but the site has no CMS Azure event provider ({string.Join(" or ", EventAssemblies)}).");
            add.Invoke(null, [services, null]);
            PostConfigure(services, options, o =>
            {
                Set(o, "ConnectionString", events.ConnectionString);
                Set(o, "Endpoint", null);
                Set(o, "TopicName", events.Topic);
            });
            Console.Error.WriteLine($"[opticli] sealed apply: production's event bus for this run, {events}");
        }
        if (settings.Blobs is { } blobs)
        {
            var (add, options) = Find("AddAzureBlobProvider", BlobAssemblies)
                ?? throw new InvalidOperationException($"[opticli] Refusing to start: production's blob storage was given for this run, but the site has no CMS Azure blob provider ({string.Join(" or ", BlobAssemblies)}).");
            add.Invoke(null, [services, null]);
            PostConfigure(services, options, o =>
            {
                Set(o, "ConnectionString", blobs.ConnectionString);
                Set(o, "ServiceUri", null);
                Set(o, "ContainerName", blobs.Container);
            });
            services.PostConfigure<BlobProvidersOptions>(o =>
            {
                o.DefaultProvider = BlobProviderName;
                if (!o.Providers.ContainsKey(BlobProviderName))
                {
                    o.AddProvider(BlobProviderName, options.Assembly.GetType(BlobProviderType)?.AssemblyQualifiedName ?? BlobProviderType);
                }
            });
            Console.Error.WriteLine($"[opticli] sealed apply: production's blob storage for this run, {blobs}");
        }
    }

    /// <summary>What the site runs with, for <see cref="AgentRoutes.Sealed"/>.</summary>
    public static SealedStatus Status(IServiceProvider services, AgentSettings settings) =>
        new(settings.Events is { } events ? EventsStatus(services, events) : null, settings.Blobs is { } blobs ? BlobsStatus(services, blobs) : null);

    private static SealedEvents EventsStatus(IServiceProvider services, AzureEvents given)
    {
        var provider = ActiveEventProvider(services);
        var options = OptionsValue(services, EventOptionsType, EventAssemblies);
        // The CMS connects by Endpoint (managed identity) instead when one is set: then the typed string isn't what it uses.
        var matches = options is not null && string.Equals(Get(options, "ConnectionString") as string, given.ConnectionString, StringComparison.Ordinal)
            && string.IsNullOrEmpty(Get(options, "Endpoint") as string);
        var topic = options is null ? null : Get(options, "TopicName") as string;
        if (provider is null || provider.GetType().FullName != EventProviderType)
        {
            return new(provider?.GetType().FullName, topic, null, false, matches, "The site's event provider isn't the CMS's Azure one: something in the site registers another.");
        }
        var inner = Inner(provider);
        if (inner is null)
        {
            return new(EventProviderType, topic, null, false, matches, "The Azure event provider's Service Bus client wasn't found (another EPiServer.Azure version).");
        }
        return new(EventProviderType, topic, Get(inner, "SubscriptionName") as string, Connected(inner), matches);
    }

    private static SealedBlobs BlobsStatus(IServiceProvider services, AzureBlobs given)
    {
        var providers = services.GetService<IOptions<BlobProvidersOptions>>()?.Value;
        var name = providers?.DefaultProvider;
        var type = name is not null && providers!.Providers.TryGetValue(name, out var typeName) ? TypeName(typeName) : null;
        var options = OptionsValue(services, BlobOptionsType, BlobAssemblies);
        // The CMS connects by ServiceUri (managed identity) instead when one is set.
        var matches = options is not null && string.Equals(Get(options, "ConnectionString") as string, given.ConnectionString, StringComparison.Ordinal)
            && Get(options, "ServiceUri") is null;
        return new(name, type, options is null ? null : Get(options, "ContainerName") as string, matches,
            options is null ? "The Azure blob provider's options weren't found." : null);
    }

    /// <summary>
    /// Sends what the Azure event provider still has queued and waits until it is sent: completes its send block (nothing
    /// is sent after it; the site stops next) and awaits it. Without this the run's last cache drops are lost, as the CMS
    /// cancels the queue when the site stops.
    /// </summary>
    public static async Task<EventsFlushResult> FlushAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var provider = ActiveEventProvider(services);
        if (provider?.GetType().FullName != EventProviderType || Inner(provider) is not { } inner)
        {
            return new(false, "The site's event provider isn't the CMS's Azure one.");
        }
        if (!Connected(inner) || Member(inner, "_sendProcessor") is not { } block)
        {
            return new(false, "The Azure event provider never connected to the bus: what it queued wasn't sent.");
        }
        await QuietAsync(services, cancellationToken);
        var complete = block.GetType().GetMethod("Complete", Type.EmptyTypes);
        if (complete is null || block.GetType().GetProperty("Completion")?.GetValue(block) is not Task completion)
        {
            return new(false, "The Azure event provider's send queue couldn't be completed (another EPiServer.Azure version).");
        }
        complete.Invoke(block, null);
        var finished = await Task.WhenAny(completion, Task.Delay(FlushTimeout, cancellationToken));
        Console.Error.WriteLine(AgentProtocol.EventsFlushedLine);
        return finished == completion
            ? completion.IsCompletedSuccessfully ? new(true) : new(false, "The Azure event provider's send queue stopped with an error; see the site's log.")
            : new(false, $"The Azure event provider hadn't sent its queue after {FlushTimeout.TotalSeconds:0} s.");
    }

    /// <summary>Waits until the site has raised no cache removal of its own for <see cref="RaiseQuiet"/> (removals from other servers don't count).</summary>
    private static async Task QuietAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var cacheEvent = (services.GetService(typeof(EPiServer.Events.Clients.IEventRegistry)) as EPiServer.Events.Clients.IEventRegistry)?.Get(RemoveFromCacheEventId);
        long Raised() => cacheEvent is null ? 0 : cacheEvent.RaisedCount - cacheEvent.ReceivedCount;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var quiet = System.Diagnostics.Stopwatch.StartNew();
        var last = Raised();
        while (quiet.Elapsed < RaiseQuiet && waited.Elapsed < RaiseQuietMax)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            if (Raised() is var now && now != last)
            {
                last = now;
                quiet.Restart();
            }
        }
    }

    /// <summary>The event provider the CMS uses: CMS 12 through <c>IEventProviderAccessor</c>, CMS 13 the registered one.</summary>
    private static object? ActiveEventProvider(IServiceProvider services) =>
#if CMS13
        services.GetService(typeof(EventProvider));
#else
        (services.GetService(typeof(IEventProviderAccessor)) as IEventProviderAccessor)?.Provider
        ?? services.GetService(typeof(EventProvider));
#endif

    /// <summary>The provider's Service Bus client (<c>DefaultServiceBusEventProvider</c>).</summary>
    internal static object? Inner(object provider)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = provider.GetType();
        var field = type.GetFields(flags).FirstOrDefault(f => f.FieldType.Name == "DefaultServiceBusEventProvider");
        if (field is not null)
        {
            return field.GetValue(provider);
        }
        return type.GetProperties(flags).FirstOrDefault(p => p.PropertyType.Name == "DefaultServiceBusEventProvider" && p.GetIndexParameters().Length == 0)?.GetValue(provider);
    }

    /// <summary>Whether the client sends: its queue is a sending block, not the buffer it starts with.</summary>
    internal static bool Connected(object inner) =>
        Member(inner, "_sendProcessor")?.GetType().Name.StartsWith("ActionBlock", StringComparison.Ordinal) == true;

    /// <summary>The <c>AddAzure…Provider(IServiceCollection, Action&lt;TOptions&gt;)</c> method and its options type.</summary>
    internal static (MethodInfo Add, Type Options)? Find(string method, IEnumerable<string> assemblies)
    {
        foreach (var assembly in assemblies.Select(Load).OfType<Assembly>())
        {
            var found = ExportedTypes(assembly)
                .Where(t => t.IsAbstract && t.IsSealed)
                .Select(t => t.GetMethod(method, BindingFlags.Public | BindingFlags.Static))
                .FirstOrDefault(m => m is not null && m.GetParameters() is [{ ParameterType: var first }, { ParameterType: { IsGenericType: true } second }]
                    && first == typeof(IServiceCollection) && second.GetGenericTypeDefinition() == typeof(Action<>));
            if (found is not null)
            {
                return (found, found.GetParameters()[1].ParameterType.GetGenericArguments()[0]);
            }
        }
        return null;
    }

    private static Assembly? Load(string name)
    {
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private static IEnumerable<Type> ExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static object? OptionsValue(IServiceProvider services, string typeName, IEnumerable<string> assemblies)
    {
        var type = assemblies.Select(Load).OfType<Assembly>().Select(a => a.GetType(typeName)).FirstOrDefault(t => t is not null);
        if (type is null)
        {
            return null;
        }
        var options = services.GetService(typeof(IOptions<>).MakeGenericType(type));
        return options?.GetType().GetProperty("Value")?.GetValue(options);
    }

    private static void PostConfigure(IServiceCollection services, Type options, Action<object> configure) =>
        typeof(SealedAzure).GetMethod(nameof(PostConfigureOf), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(options).Invoke(null, [services, configure]);

    private static void PostConfigureOf<T>(IServiceCollection services, Action<object> configure) where T : class =>
        services.PostConfigure<T>(o => configure(o));

    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.SetValue(target, value);

    private static object? Get(object target, string property) =>
        target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);

    private static object? Member(object target, string field) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target);

    /// <summary>A type's full name from an assembly-qualified one.</summary>
    private static string TypeName(string assemblyQualified) => assemblyQualified.Split(',')[0].Trim();
}
