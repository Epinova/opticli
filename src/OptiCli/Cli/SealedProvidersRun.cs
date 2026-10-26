using System.Diagnostics;
using OptiCli.Core.Azure;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Seals;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Cli;

/// <summary>What the run did about production's servers' caches: sent the cache drops over the event bus, or not (restart).</summary>
/// <param name="Sent">How many of the run's messages reached the topic (counted on a probe subscription).</param>
/// <param name="Problem">Why the events can't be trusted to have reached production's servers; null when they did.</param>
internal sealed record SealedEventsResult(string Topic, string? Subscription, int Subscribers, int Sent, string? Problem);

/// <summary>An uploaded file, looked up in production's blob storage after the run.</summary>
/// <param name="Blob">Its name in the container (<c>&lt;guid&gt;/&lt;file&gt;</c>); null when the version names no file.</param>
/// <param name="Bytes">The file's size as the plan has it.</param>
/// <param name="Stored">The blob's size; null when it isn't there.</param>
/// <param name="Problem">Why it couldn't be checked (the service or the database didn't answer); it then counts as not found.</param>
internal sealed record SealedMediaResult(int Step, Guid? Guid, string? Ref, string? Blob, long Bytes, long? Stored, string? Problem = null)
{
    public bool Found => Problem is null && Stored == Bytes;
}

/// <summary>
/// Production's event bus and blob storage in a sealed run (phase 5 of the content-sync plan): asked at the terminal after
/// the write login, checked before the seal is used, compared with what the run's site has in effect before anything is
/// written, then the run's cache drops sent and counted, and every uploaded file looked up in the container.
/// </summary>
/// <remarks>
/// Only what the user types reaches this: no option, environment variable or configuration entry. The connection strings go
/// to the site on its stdin line (<see cref="SealedSite"/>) and are never printed, logged or written.
/// </remarks>
internal sealed class SealedProvidersRun(ITerminal terminal, SealedProviders providers, int subscribers)
{
    /// <summary><c>details.reason</c> when production's event bus can't be used.</summary>
    public const string EventsReason = "eventBus";

    /// <summary><c>details.reason</c> when production's blob storage can't be used.</summary>
    public const string BlobsReason = "blobStorage";

    /// <summary><c>details.reason</c> when an uploaded file isn't in production's blob storage after the run.</summary>
    public const string BlobMissingReason = "blobMissing";

    /// <summary>Production's topic (the Service Bus SDK); tests put a stand-in here. Nothing on the command line or in the environment sets it.</summary>
    internal static Func<ServiceBusConnection, string, IEventBusTopic> Topics { get; set; } = (connection, topic) => new ServiceBusTopic(connection, topic);

    /// <summary>Production's container (the Blob Storage SDK); tests put a stand-in here.</summary>
    internal static Func<BlobConnection, string, IMediaContainer> Containers { get; set; } = (connection, container) => new BlobContainer(connection, container);

    /// <summary>How long the run waits for the site's event provider to connect and make its subscription.</summary>
    internal static TimeSpan SubscriptionWait { get; set; } = TimeSpan.FromSeconds(60);

    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the probe must stay quiet before its count is final.</summary>
    internal static TimeSpan ProbeQuiet { get; set; } = TimeSpan.FromSeconds(5);

    private string? _subscription;
    private string? _probe;

    public SealedProviders Providers => providers;

    /// <summary>
    /// A plan that uploads needs production's blob storage, which needs the CMS's Azure blob provider in the build: refused before
    /// anything is asked otherwise.
    /// </summary>
    /// <exception cref="RefusedException">The plan uploads and the build has no Azure blob provider.</exception>
    public static void RequireMediaPossible(WritePlan plan, string outputFolder)
    {
        if (!SiteAzure.HasBlobProvider(outputFolder))
        {
            SealedPlan.RequireNoMedia(plan, "The site's build has no CMS Azure blob provider (EPiServer.Azure, or EPiServer.Blobs.Azure on CMS 13), so the run can't write files to production's blob storage.");
        }
    }

    /// <summary>
    /// Asks for production's event bus (when the build has the Azure event provider; optional) and blob storage (when the plan
    /// uploads; required then), and checks both before the seal is used.
    /// </summary>
    /// <param name="localProduction">The production database is on this machine (a test): only then may blob storage be Azurite.</param>
    /// <returns>Null when neither was given.</returns>
    /// <exception cref="UsageException">A connection string of the wrong form, or a key the service refused.</exception>
    /// <exception cref="RefusedException">The topic is missing or has no subscriptions; the container is missing; uploads without blob storage.</exception>
    public static async Task<SealedProvidersRun?> AskAsync(ITerminal terminal, WritePlan plan, string projectDirectory, string outputFolder, bool localProduction, CancellationToken cancellationToken)
    {
        ServiceBusConnection? events = null;
        string? topic = null;
        var subscribers = 0;
        if (SiteAzure.HasEventProvider(outputFolder))
        {
            terminal.WriteLine();
            terminal.WriteLine("Production's event bus (Azure Service Bus), so production's servers drop what they cached and show the changes at once (used for this run only, never saved):");
            var value = terminal.ReadHidden("  Connection string (not shown; Enter for none: then restart the site after the run): ");
            if (string.IsNullOrWhiteSpace(value))
            {
                terminal.WriteLine("  None: production's servers keep what they cached until the site restarts.");
            }
            else
            {
                events = Parse(() => ServiceBusConnection.Parse(value));
                topic = Ask(terminal, "  Topic", SiteAzure.Setting(projectDirectory, SiteAzure.TopicSetting) ?? SiteAzure.DefaultTopic);
                await using var bus = Topics(events, topic);
                var subscriptions = await Unused(() => bus.SubscriptionsAsync(cancellationToken))
                    ?? throw new RefusedException($"Production's event bus ({events.Endpoint.Host}) has no topic '{topic}'. Nothing was written and the seal is still unused.",
                        $"Give the topic production's site uses ({SiteAzure.TopicSetting} in its settings on the server), or press Enter for no event bus.") { Details = new { reason = EventsReason, topic } };
                if (subscriptions.Count == 0)
                {
                    throw new RefusedException($"The topic '{topic}' on {events.Endpoint.Host} has no subscriptions: no server of production listens on it, so it is the wrong topic or bus (or production isn't running). Nothing was written and the seal is still unused.",
                        "Each of production's servers makes a subscription on its topic when it starts. Give the bus and topic production's site uses, or press Enter for no event bus.") { Details = new { reason = EventsReason, topic } };
                }
                subscribers = subscriptions.Count;
                // The servers' names (machine name and 8 random characters) show whose topic it is: check it is production's.
                terminal.WriteLine($"  Topic '{Printable.Of(topic)}' on {events.Endpoint.Host}: {subscribers} subscription(s), one per server listening: {Printable.Of(string.Join(", ", subscriptions.Take(8)))}{(subscribers > 8 ? ", …" : "")}. Make sure these are production's servers.");
            }
        }

        BlobConnection? blobs = null;
        string? container = null;
        var uploads = SealedPlan.Uploads(plan).Count;
        if (uploads > 0)
        {
            terminal.WriteLine();
            terminal.WriteLine($"The plan uploads {uploads} media file(s). Production's blob storage (Azure Storage; used for this run only, never saved):");
            var value = terminal.ReadHidden("  Connection string (not shown; Enter cancels, the seal stays unused): ");
            if (string.IsNullOrWhiteSpace(value))
            {
                SealedPlan.RequireNoMedia(plan, "No blob storage was given, so nothing was written and the seal is still unused.");
            }
            blobs = Parse(() => BlobConnection.Parse(value));
            if (blobs.IsDevelopment && !localProduction)
            {
                throw new UsageException("Storage on this machine (UseDevelopmentStorage, Azurite) isn't production's blob storage. Nothing was written and the seal is still unused.");
            }
            container = Ask(terminal, "  Container", SiteAzure.Setting(projectDirectory, SiteAzure.ContainerSetting) ?? SiteAzure.DefaultContainer);
            var store = Containers(blobs, container);
            if (!await Unused(() => store.ExistsAsync(cancellationToken)))
            {
                throw new RefusedException($"Production's blob storage ({blobs.Endpoint.Host}) has no container '{container}'. Nothing was written and the seal is still unused.",
                    $"Give the container production's site uses ({SiteAzure.ContainerSetting} in its settings on the server).") { Details = new { reason = BlobsReason, container } };
            }
            terminal.WriteLine($"  Container '{Printable.Of(container)}' on {blobs.Endpoint.Host}: found.");
        }
        return events is null && blobs is null ? null : new SealedProvidersRun(terminal, new SealedProviders(events, topic, blobs, container), subscribers);
    }

    /// <summary>The site runs with what the user gave, and nothing of its own instead: checked once it answers, before anything is written.</summary>
    /// <exception cref="RefusedException">It doesn't (the site is then stopped).</exception>
    /// <param name="status">The site's <see cref="AgentClient.SealedStatusAsync"/>.</param>
    public async Task RequireInEffectAsync(Func<CancellationToken, Task<SealedStatus>> status, CancellationToken cancellationToken)
    {
        var problems = Problems(await status(cancellationToken), providers).ToList();
        if (problems.Count > 0)
        {
            var events = problems.Any(p => p.StartsWith("Events", StringComparison.Ordinal));
            throw new RefusedException($"Stopped the site before writing: {string.Join(" ", problems)}",
                events
                    ? "Something in the site registers its own event provider in Development, or sets its options after startup: find it, or run without the event bus (Enter) and restart production after the run. The seal is used: seal again."
                    : "Something in the site registers its own blob provider in Development, or sets its options after startup: find it, or leave the uploads out and upload the files in production's edit UI. The seal is used: seal again.")
            {
                Details = new { reason = events ? EventsReason : BlobsReason, problems },
            };
        }
    }

    internal static IEnumerable<string> Problems(SealedStatus status, SealedProviders providers)
    {
        if (providers.Events is not null)
        {
            if (status.Events is not { } events)
            {
                yield return "Events: the site reports no event bus for this run (an agent from another opticli version?).";
            }
            else
            {
                if (events.Provider != "EPiServer.Azure.Events.AzureEventProvider")
                {
                    yield return $"Events: the site's event provider is {events.Provider ?? "none"}, not the CMS's Azure one.{(events.Problem is { } p ? $" {p}" : "")}";
                }
                if (!events.ConnectionMatches)
                {
                    yield return "Events: the event provider's connection string isn't the one typed for this run (or an Endpoint set by the site takes its place).";
                }
                if (!string.Equals(events.Topic, providers.Topic, StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"Events: the event provider uses topic '{events.Topic}', not '{providers.Topic}'.";
                }
            }
        }
        if (providers.Blobs is not null)
        {
            if (status.Blobs is not { } blobs)
            {
                yield return "Blobs: the site reports no blob storage for this run (an agent from another opticli version?).";
            }
            else
            {
                if (!string.Equals(blobs.DefaultProvider, "azure", StringComparison.OrdinalIgnoreCase) || blobs.ProviderType != "EPiServer.Azure.Blobs.AzureBlobProvider")
                {
                    yield return $"Blobs: the site's default blob provider is '{blobs.DefaultProvider}' ({blobs.ProviderType ?? "unknown type"}), not the CMS's Azure one: files would land elsewhere.";
                }
                if (!blobs.ConnectionMatches)
                {
                    yield return "Blobs: the blob provider's connection string isn't the one typed for this run (or a ServiceUri set by the site takes its place).";
                }
                if (!string.Equals(blobs.Container, providers.Container, StringComparison.Ordinal))
                {
                    yield return $"Blobs: the blob provider uses container '{blobs.Container}', not '{providers.Container}'.";
                }
            }
        }
    }

    /// <summary>
    /// Right before the first save: the site's event provider has connected and its subscription is on the topic (else the run
    /// stops: its cache drops would go nowhere), and a probe subscription that counts the run's messages is made.
    /// </summary>
    /// <exception cref="RefusedException">Not within <see cref="SubscriptionWait"/>; nothing is written.</exception>
    /// <param name="status">The site's <see cref="AgentClient.SealedStatusAsync"/>.</param>
    public async Task BeforeSavingAsync(Func<CancellationToken, Task<SealedStatus>> status, string probeName, CancellationToken cancellationToken)
    {
        if (providers.Events is not { } connection)
        {
            return;
        }
        await using var bus = Topics(connection, providers.Topic!);
        var waited = Stopwatch.StartNew();
        string? last = null;
        while (true)
        {
            var events = (await status(cancellationToken)).Events;
            if (events is { Connected: true, Subscription: { } subscription })
            {
                if (await bus.SubscriptionExistsAsync(subscription, cancellationToken))
                {
                    _subscription = subscription;
                    break;
                }
                last = $"the run's subscription '{subscription}' isn't on topic '{providers.Topic}'";
            }
            else
            {
                last = events?.Problem ?? "the site's event provider hasn't connected to the bus";
            }
            if (waited.Elapsed >= SubscriptionWait)
            {
                throw new RefusedException($"Stopped before writing: {last} after {SubscriptionWait.TotalSeconds:0} s, so production's servers wouldn't hear about the run's changes.",
                    "See the run's log for the event provider's errors (a key without Manage, a firewall). Or run again without the event bus (Enter) and restart production afterwards; the seal is used, so seal again.")
                {
                    Details = new { reason = EventsReason },
                };
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
        await bus.CreateProbeAsync(probeName, _subscription, cancellationToken);
        _probe = probeName;
        terminal.WriteLine($"The site is on production's event bus (subscription '{Printable.Of(_subscription)}' on topic '{Printable.Of(providers.Topic!)}').");
    }

    /// <summary>
    /// After the saves (also after a partial run), while the site still runs: sends what its event provider queued, counts the
    /// run's messages on the probe, and deletes it. Never throws: what went wrong is the result's problem (a restart is needed).
    /// </summary>
    /// <param name="flush">The site's <see cref="AgentClient.FlushEventsAsync"/>.</param>
    public async Task<SealedEventsResult?> FinishEventsAsync(Func<CancellationToken, Task<EventsFlushResult>> flush)
    {
        if (providers.Events is not { } connection)
        {
            return null;
        }
        if (_probe is null)
        {
            return new(providers.Topic!, _subscription, subscribers, 0, "the run stopped before the event bus was checked");
        }
        string? problem = null;
        var sent = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bus = Topics(connection, providers.Topic!);
        try
        {
            var flushed = await flush(timeout.Token);
            if (!flushed.Flushed)
            {
                problem = flushed.Problem ?? "the site's event provider didn't send what it queued";
            }
            sent = await bus.DrainAsync(_probe, ProbeQuiet, TimeSpan.FromMinutes(2), timeout.Token);
            if (problem is null && sent == 0)
            {
                problem = "none of the run's messages reached the topic";
            }
        }
        catch (Exception ex) when (ex is OptiCliException or HttpRequestException or OperationCanceledException or System.Xml.XmlException)
        {
            problem ??= $"the run's messages couldn't be counted: {ex.Message}";
        }
        try
        {
            await bus.DeleteSubscriptionAsync(_probe, timeout.Token);
        }
        catch (Exception ex) when (ex is OptiCliException or HttpRequestException or OperationCanceledException)
        {
            // Service Bus deletes it after five idle minutes.
        }
        return new(providers.Topic!, _subscription, subscribers, sent, problem);
    }

    /// <summary>Each uploaded file that saved, looked up in production's blob storage by the blob its new version names.</summary>
    public Task<IReadOnlyList<SealedMediaResult>> CheckMediaAsync(ContentSession session, PlanRun run, CancellationToken cancellationToken) =>
        CheckMediaAsync(async (version, token) => (await VersionReader.ByIdAsync(session.Db, session.Model, version, token))?.BlobUri, run, cancellationToken);

    /// <param name="blobUri">The blob URI a version of production's names (<c>tblWorkContent.BlobUri</c>).</param>
    /// <remarks>Never throws: it runs after the saves, whose result must reach the user; a file that can't be checked has a <see cref="SealedMediaResult.Problem"/>.</remarks>
    internal async Task<IReadOnlyList<SealedMediaResult>> CheckMediaAsync(Func<int, CancellationToken, Task<string?>> blobUri, PlanRun run, CancellationToken cancellationToken)
    {
        if (providers.Blobs is not { } connection)
        {
            return [];
        }
        var store = Containers(connection, providers.Container!);
        var results = new List<SealedMediaResult>();
        foreach (var step in run.Operations.Where(o => o.Op == "upload" && o.Status == PlanStepStatus.Saved))
        {
            var output = step.Result as WriteOutput;
            var bytes = output?.Upload?.Bytes ?? 0;
            string? blob = null;
            try
            {
                if (output?.Version is { } version && int.TryParse(version.Split('_').Last(), out var versionId)
                    && await blobUri(versionId, cancellationToken) is { } uri
                    && BlobLocator.ParseUri(uri) is (_, { } folder, { } file))
                {
                    blob = $"{folder}/{file}";
                }
                results.Add(new SealedMediaResult(step.Index, output?.Guid ?? step.Guid, output?.Ref, blob, bytes, blob is null ? null : await store.SizeAsync(blob, cancellationToken)));
            }
            catch (Exception ex) when (ex is OptiCliException or HttpRequestException or OperationCanceledException or System.Data.Common.DbException or InvalidOperationException)
            {
                results.Add(new SealedMediaResult(step.Index, output?.Guid ?? step.Guid, output?.Ref, blob, bytes, null, $"not checked: {ex.Message}"));
            }
        }
        return results;
    }

    /// <summary>A check before the seal is used: a failure says so (the service didn't answer, or refused the key).</summary>
    private static async Task<T> Unused<T>(Func<Task<T>> check)
    {
        try
        {
            return await check();
        }
        catch (Exception ex) when (ex is Core.Errors.UnreachableException or UsageException)
        {
            throw OptiCliException.Create(((OptiCliException)ex).Code, $"{ex.Message} Nothing was written and the seal is still unused.", ((OptiCliException)ex).Hint, ((OptiCliException)ex).Details);
        }
    }

    private static T Parse<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (UsageException ex)
        {
            throw new UsageException($"{ex.Message} Nothing was written and the seal is still unused.", ex.Hint);
        }
    }

    private static string Ask(ITerminal terminal, string question, string fallback)
    {
        terminal.Write($"{question} [{Printable.Of(fallback)}]: ");
        var typed = terminal.ReadLine()?.Trim();
        return string.IsNullOrEmpty(typed) ? fallback : typed;
    }
}
