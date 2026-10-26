using OptiCli.Cli;
using OptiCli.Core.Azure;
using OptiCli.Core.Errors;
using OptiCli.Core.Seals;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Tests;

/// <summary>
/// Production's event bus and blob storage in a sealed run (<see cref="SealedProvidersRun"/>), with stand-ins for the person
/// at the terminal, the topic and the container: what is asked and when, what is checked before the seal is used, before the
/// first save and after the run, and that a restart is reported whenever the events can't be confirmed.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SealedProvidersRunTests : IDisposable
{
    private const string Bus = "Endpoint=sb://prod-bus.servicebus.windows.net/;SharedAccessKeyName=manage;SharedAccessKey=bus-secret";
    private const string Storage = "DefaultEndpointsProtocol=https;AccountName=prodmedia;AccountKey=YmxvYi1zZWNyZXQ=;EndpointSuffix=core.windows.net";
    private const string Page = "61104228-962d-4dd2-925c-b5b12e2373ee";
    private const string Image = "0d6f3a52-8b1e-4c7a-9e25-5a4c1b7d3f90";
    private const string Subscription = "DEVBOX_1a2b3c4d";

    private readonly Folder _folder = new();
    private readonly FakeTerminal _terminal = new();
    private readonly FakeTopic _topic = new();
    private readonly FakeContainer _container = new();

    public SealedProvidersRunTests()
    {
        SealedProvidersRun.Topics = (connection, topic) => _topic.For(connection, topic);
        SealedProvidersRun.Containers = (connection, container) => _container.For(connection, container);
        SealedProvidersRun.PollInterval = TimeSpan.FromMilliseconds(10);
        SealedProvidersRun.ProbeQuiet = TimeSpan.FromSeconds(1);
    }

    public void Dispose()
    {
        SealedProvidersRun.Topics = (connection, topic) => new ServiceBusTopic(connection, topic);
        SealedProvidersRun.Containers = (connection, container) => new BlobContainer(connection, container);
        SealedProvidersRun.PollInterval = TimeSpan.FromSeconds(2);
        SealedProvidersRun.ProbeQuiet = TimeSpan.FromSeconds(5);
        SealedProvidersRun.SubscriptionWait = TimeSpan.FromSeconds(60);
        _folder.Dispose();
    }

    private string Output => _folder.Combine("bin");

    private void Build(params string[] assemblies)
    {
        Directory.CreateDirectory(Output);
        foreach (var assembly in assemblies)
        {
            File.WriteAllText(Path.Combine(Output, assembly), "");
        }
    }

    private static WritePlan Plan(bool upload = false) => WritePlan.Parse(upload
        ? $$$"""{"operations": [{"op": "upload", "id": "logo", "guid": "{{{Image}}}", "file": "logo.png", "parent": "{{{Page}}}"}, {"op": "set", "ref": "{{{Page}}}", "properties": {"Heading": "x"}}]}"""
        : $$$"""{"operations": [{"op": "set", "ref": "{{{Page}}}", "properties": {"Heading": "x"}}]}""");

    private Task<SealedProvidersRun?> AskAsync(WritePlan plan, bool localProduction = false) =>
        SealedProvidersRun.AskAsync(_terminal, plan, _folder.Path, Output, localProduction, CancellationToken.None);

    [Fact]
    public async Task A_site_without_the_azure_providers_is_asked_nothing()
    {
        Build();

        Assert.Null(await AskAsync(Plan()));
        Assert.Equal("", _terminal.Output);
        Assert.Empty(_topic.Calls);
    }

    [Fact]
    public async Task Enter_at_the_event_bus_means_none_and_a_restart()
    {
        Build("EPiServer.Azure.dll");

        Assert.Null(await AskAsync(Plan()));
        Assert.Contains("Production's event bus (Azure Service Bus)", _terminal.Output, StringComparison.Ordinal);
        Assert.Contains("until the site restarts", _terminal.Output, StringComparison.Ordinal);
        Assert.Empty(_topic.Calls);
    }

    [Fact]
    public async Task The_event_bus_is_checked_for_its_topic_and_subscribers_and_never_shown()
    {
        Build("EPiServer.Events.Azure.dll");
        _folder.Write("appsettings.json", """{"EPiServer": {"Cms": {"AzureEventProvider": {"TopicName": "SiteEvents"}}}}""");
        _terminal.Hidden.Enqueue(Bus);

        var run = await AskAsync(Plan());

        Assert.Equal(("SiteEvents", (BlobConnection?)null), (run!.Providers.Topic, run.Providers.Blobs));
        Assert.Equal("prod-bus.servicebus.windows.net", run.Providers.Events!.Endpoint.Host);
        Assert.Equal(("prod-bus.servicebus.windows.net", "SiteEvents"), (_topic.Connection!.Endpoint.Host, _topic.Name));
        // The topic from the site's settings was offered, and Enter took it.
        Assert.Contains("Topic [SiteEvents]: ", _terminal.Output, StringComparison.Ordinal);
        // The servers' names, so the user can tell it is production's topic.
        Assert.Contains("2 subscription(s), one per server listening: WEB0_aaaa1111, WEB1_bbbb2222", _terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("bus-secret", _terminal.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "has no topic 'SiteEvents'")]
    [InlineData(true, "has no subscriptions")]
    public async Task A_missing_topic_or_one_nobody_listens_on_is_refused_before_the_seal_is_used(bool topicExists, string expected)
    {
        Build("EPiServer.Azure.dll");
        _topic.Subscriptions = topicExists ? [] : null;
        _terminal.Hidden.Enqueue(Bus);
        _terminal.Lines.Enqueue("SiteEvents");

        var refused = await Assert.ThrowsAsync<RefusedException>(() => AskAsync(Plan()));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.Contains("seal is still unused", refused.Message, StringComparison.Ordinal);
        Assert.Equal(SealedProvidersRun.EventsReason, Reason(refused));
    }

    [Fact]
    public async Task A_bus_that_doesnt_answer_says_the_seal_is_unused()
    {
        Build("EPiServer.Azure.dll");
        _topic.Fail = new UnreachableException("Production's event bus (prod-bus.servicebus.windows.net) couldn't list the topic's subscriptions: Name or service not known.");
        _terminal.Hidden.Enqueue(Bus);

        var failed = await Assert.ThrowsAsync<UnreachableException>(() => AskAsync(Plan()));

        Assert.EndsWith("Nothing was written and the seal is still unused.", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_string_without_a_key_is_refused_with_the_seal_unused()
    {
        Build("EPiServer.Azure.dll");
        _terminal.Hidden.Enqueue("Endpoint=sb://prod-bus.servicebus.windows.net/;Authentication=Managed Identity");

        var refused = await Assert.ThrowsAsync<UsageException>(() => AskAsync(Plan()));

        Assert.Contains("seal is still unused", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Entra ID", refused.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Uploads_are_refused_when_the_build_has_no_azure_blob_provider()
    {
        Build("EPiServer.Events.Azure.dll");

        var refused = Assert.Throws<RefusedException>(() => SealedProvidersRun.RequireMediaPossible(Plan(upload: true), Output));

        Assert.Equal(SealedPlan.Reason, Reason(refused));
        Assert.Contains("no CMS Azure blob provider", refused.Hint, StringComparison.Ordinal);
        SealedProvidersRun.RequireMediaPossible(Plan(), Output);
    }

    [Fact]
    public async Task Uploads_without_blob_storage_are_refused_as_before()
    {
        Build("EPiServer.Blobs.Azure.dll");

        var refused = await Assert.ThrowsAsync<RefusedException>(() => AskAsync(Plan(upload: true)));

        Assert.Equal(SealedPlan.Reason, Reason(refused));
        Assert.Contains("The plan uploads 1 media file(s)", _terminal.Output, StringComparison.Ordinal);
        Assert.Contains("seal is still unused", refused.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blob_storage_is_checked_for_its_container()
    {
        Build("EPiServer.Azure.dll");
        _terminal.Hidden.Enqueue("");
        _terminal.Hidden.Enqueue(Storage);
        _terminal.Lines.Enqueue("media");

        var run = await AskAsync(Plan(upload: true));

        Assert.Equal(("media", (ServiceBusConnection?)null), (run!.Providers.Container, run.Providers.Events));
        Assert.Equal("media", _container.Name);
        Assert.Contains("Container [mysitemedia]: ", _terminal.Output, StringComparison.Ordinal);
        Assert.Contains("Container 'media' on prodmedia.blob.core.windows.net: found.", _terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("YmxvYi1zZWNyZXQ", _terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_container_or_storage_on_this_machine_for_a_remote_production_is_refused()
    {
        Build("EPiServer.Blobs.Azure.dll");
        _container.Exists = false;
        _terminal.Hidden.Enqueue(Storage);
        _terminal.Lines.Enqueue("media");
        var missing = await Assert.ThrowsAsync<RefusedException>(() => AskAsync(Plan(upload: true)));

        _terminal.Hidden.Enqueue("UseDevelopmentStorage=true");
        var azurite = await Assert.ThrowsAsync<UsageException>(() => AskAsync(Plan(upload: true)));

        Assert.Equal(SealedProvidersRun.BlobsReason, Reason(missing));
        Assert.Contains("isn't production's blob storage", azurite.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_site_runs_with_must_be_what_was_typed()
    {
        var providers = Providers(events: true, blobs: true);
        var good = new SealedStatus(
            new SealedEvents("EPiServer.Azure.Events.AzureEventProvider", "siteevents", Subscription, true, true),
            new SealedBlobs("azure", "EPiServer.Azure.Blobs.AzureBlobProvider", "media", true));

        Assert.Empty(SealedProvidersRun.Problems(good, providers));
        Assert.Single(SealedProvidersRun.Problems(good with { Events = good.Events! with { Provider = "EPiServer.Events.Providers.Internal.NullEventProvider" } }, providers));
        Assert.Single(SealedProvidersRun.Problems(good with { Events = good.Events! with { ConnectionMatches = false } }, providers));
        Assert.Single(SealedProvidersRun.Problems(good with { Events = good.Events! with { Topic = "OtherEvents" } }, providers));
        Assert.Single(SealedProvidersRun.Problems(good with { Blobs = good.Blobs! with { DefaultProvider = "default", ProviderType = "EPiServer.Framework.Blobs.FileBlobProvider" } }, providers));
        Assert.Single(SealedProvidersRun.Problems(good with { Blobs = good.Blobs! with { Container = "other" } }, providers));
        Assert.Equal(2, SealedProvidersRun.Problems(new SealedStatus(null, null), providers).Count());
    }

    [Fact]
    public async Task Before_the_first_save_the_run_waits_for_its_subscription_and_makes_the_probe()
    {
        var statuses = new Queue<SealedEvents>([
            new("EPiServer.Azure.Events.AzureEventProvider", "SiteEvents", Subscription, Connected: false, true),
            new("EPiServer.Azure.Events.AzureEventProvider", "SiteEvents", Subscription, Connected: true, true),
        ]);
        _topic.Existing.Add(Subscription);
        var run = new SealedProvidersRun(_terminal, Providers(events: true), 2);

        await run.BeforeSavingAsync(_ => Task.FromResult(new SealedStatus(statuses.Count > 1 ? statuses.Dequeue() : statuses.Peek(), null)), "opticli-probe-x", CancellationToken.None);

        Assert.Contains($"subscription '{Subscription}'", _terminal.Output, StringComparison.Ordinal);
        Assert.Equal([("opticli-probe-x", Subscription)], _topic.Probes);
    }

    [Fact]
    public async Task Without_its_subscription_on_the_topic_nothing_is_written()
    {
        SealedProvidersRun.SubscriptionWait = TimeSpan.FromMilliseconds(100);
        var run = new SealedProvidersRun(_terminal, Providers(events: true), 2);
        var status = new SealedStatus(new SealedEvents("EPiServer.Azure.Events.AzureEventProvider", "SiteEvents", Subscription, true, true), null);

        var refused = await Assert.ThrowsAsync<RefusedException>(() => run.BeforeSavingAsync(_ => Task.FromResult(status), "opticli-probe-x", CancellationToken.None));

        Assert.Equal(SealedProvidersRun.EventsReason, Reason(refused));
        Assert.Contains($"'{Subscription}' isn't on topic 'SiteEvents'", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_topic.Probes);
    }

    [Theory]
    [InlineData(true, 3, null)]
    [InlineData(true, 0, "none of the run's messages reached the topic")]
    [InlineData(false, 2, "never connected")]
    public async Task After_the_run_the_queue_is_flushed_and_the_runs_messages_counted(bool flushed, int messages, string? problem)
    {
        var run = await ReadyAsync();
        _topic.Messages = messages;

        var events = await run.FinishEventsAsync(_ => Task.FromResult(new EventsFlushResult(flushed, flushed ? null : "The Azure event provider never connected to the bus.")));

        Assert.Equal(messages, events!.Sent);
        if (problem is null)
        {
            Assert.Null(events.Problem);
            Assert.False(SealedApply.RestartNeeded(events));
        }
        else
        {
            Assert.Contains(problem, events.Problem, StringComparison.Ordinal);
            Assert.True(SealedApply.RestartNeeded(events));
        }
        Assert.Equal(["opticli-probe-x"], _topic.Deleted);
    }

    [Fact]
    public async Task A_probe_that_cant_be_read_means_a_restart_and_is_still_deleted()
    {
        var run = await ReadyAsync();
        _topic.Fail = new UnreachableException("Production's event bus (prod-bus.servicebus.windows.net) couldn't receive from the probe subscription: AMQP blocked.");

        var events = await run.FinishEventsAsync(_ => Task.FromResult(new EventsFlushResult(true)));

        Assert.Contains("AMQP blocked", events!.Problem, StringComparison.Ordinal);
        Assert.True(SealedApply.RestartNeeded(events));
    }

    [Fact]
    public async Task A_run_that_stopped_before_the_check_or_had_no_bus_needs_a_restart()
    {
        var run = new SealedProvidersRun(_terminal, Providers(events: true), 2);

        var events = await run.FinishEventsAsync(_ => throw new InvalidOperationException("not called"));

        Assert.True(SealedApply.RestartNeeded(events));
        Assert.True(SealedApply.RestartNeeded(null));
        Assert.Empty(_topic.Calls);
    }

    [Fact]
    public async Task Each_uploaded_file_is_looked_up_where_its_new_version_points()
    {
        var run = new SealedProvidersRun(_terminal, Providers(blobs: true), 0);
        var other = Guid.NewGuid();
        _container.Blobs["0d6f3a528b1e4c7a9e255a4c1b7d3f90/logo.png"] = 1234;
        var plan = new PlanRun(false, [
            Upload(0, Guid.Parse(Image), "201_901", 1234),
            Upload(1, other, "202_902", 99),
            new PlanStepResult(2, "set", null, PlanStepStatus.Saved, null),
            Upload(3, Guid.NewGuid(), "203_903", 5) with { Status = PlanStepStatus.Unchanged },
        ], new Dictionary<string, string>());
        var uris = new Dictionary<int, string>
        {
            [901] = "epi.fx.blob://default/0d6f3a528b1e4c7a9e255a4c1b7d3f90/logo.png",
            [902] = $"epi.fx.blob://default/{other:N}/icon.svg",
        };

        var media = await run.CheckMediaAsync((version, _) => Task.FromResult(uris.GetValueOrDefault(version)), plan, CancellationToken.None);

        Assert.Equal(2, media.Count);
        Assert.Equal(("0d6f3a528b1e4c7a9e255a4c1b7d3f90/logo.png", true), (media[0].Blob, media[0].Found));
        Assert.Equal(($"{other:N}/icon.svg", (long?)null, false), (media[1].Blob, media[1].Stored, media[1].Found));
    }

    [Fact]
    public async Task A_file_that_cant_be_checked_counts_as_missing_and_nothing_throws()
    {
        var run = new SealedProvidersRun(_terminal, Providers(blobs: true), 0);
        _container.Fail = new UnreachableException("Production's blob storage (prodmedia.blob.core.windows.net) couldn't read blob x (500).");
        var plan = new PlanRun(false, [Upload(0, Guid.Parse(Image), "201_901", 1234), Upload(1, Guid.NewGuid(), "202_902", 5)], new Dictionary<string, string>());

        var media = await run.CheckMediaAsync((version, _) => version == 902 ? throw new InvalidOperationException("database gone") : Task.FromResult<string?>("epi.fx.blob://default/0d6f3a528b1e4c7a9e255a4c1b7d3f90/logo.png"), plan, CancellationToken.None);

        Assert.All(media, m => Assert.False(m.Found));
        Assert.Contains("couldn't read blob", media[0].Problem, StringComparison.Ordinal);
        Assert.Contains("database gone", media[1].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_the_site_couldnt_send_means_a_restart()
    {
        var log = Path.Combine(_folder.Path, "run.log");
        var sent = new SealedEventsResult("SiteEvents", Subscription, 2, 12, null);
        File.WriteAllText(log, "info: started\n");
        Assert.Equal(sent, SealedApply.SendFailures(sent, log));

        File.AppendAllText(log, "fail: EPiServer.Azure.Events.AzureEventProvider[182540342]\n      Failed to send event. EventId=9484e34b. Sequence=7, Parameter='x'.\n");
        var failed = SealedApply.SendFailures(sent, log)!;

        Assert.Contains("1 event(s) of the run it couldn't send", failed.Problem, StringComparison.Ordinal);
        Assert.True(SealedApply.RestartNeeded(failed));
        Assert.Null(SealedApply.SendFailures(null, log));

        // After the queue was sent, the site stopping: listed, not the run's.
        File.WriteAllText(log, $"info: started\n{Protocol.AgentProtocol.EventsFlushedLine}\nfail: Failed to send event. EventId=9484e34b. Sequence=50.\nfail: Failed to send event. EventId=9484e34b. Sequence=51.\n");
        var stopping = SealedApply.SendFailures(sent, log)!;
        Assert.Equal((null, 2), (stopping.Problem, stopping.NotSentWhileStopping));
        Assert.False(SealedApply.RestartNeeded(stopping));
    }

    private static PlanStepResult Upload(int index, Guid guid, string version, long bytes) =>
        new(index, "upload", null, PlanStepStatus.Saved,
            new WriteOutput("1", version, guid, "ImageFile", "logo.png", null, null, null, true, true, false, true, null, [], null) { Upload = new UploadInfo("/plan/files/logo.png", bytes, null) },
            Guid: guid);

    /// <summary>A run past the check before the first save: the probe exists.</summary>
    private async Task<SealedProvidersRun> ReadyAsync()
    {
        _topic.Existing.Add(Subscription);
        var run = new SealedProvidersRun(_terminal, Providers(events: true), 2);
        await run.BeforeSavingAsync(_ => Task.FromResult(new SealedStatus(new SealedEvents("EPiServer.Azure.Events.AzureEventProvider", "SiteEvents", Subscription, true, true), null)), "opticli-probe-x", CancellationToken.None);
        return run;
    }

    private static SealedProviders Providers(bool events = false, bool blobs = false) => new(
        events ? ServiceBusConnection.Parse(Bus) : null, events ? "SiteEvents" : null,
        blobs ? BlobConnection.Parse(Storage) : null, blobs ? "media" : null);

    private static string? Reason(OptiCliException ex) =>
        System.Text.Json.JsonSerializer.SerializeToElement(ex.Details).GetProperty("reason").GetString();

    /// <summary>Production's topic: two servers subscribed unless a test says otherwise.</summary>
    private sealed class FakeTopic : IEventBusTopic
    {
        public ServiceBusConnection? Connection { get; private set; }

        public string Name { get; private set; } = "";

        public IReadOnlyList<string>? Subscriptions { get; set; } = ["WEB0_aaaa1111", "WEB1_bbbb2222"];

        public HashSet<string> Existing { get; } = [];

        public List<(string Subscription, string Sender)> Probes { get; } = [];

        public List<string> Deleted { get; } = [];

        public List<string> Calls { get; } = [];

        public int Messages { get; set; }

        /// <summary>What every call but the delete throws, when set.</summary>
        public OptiCliException? Fail { get; set; }

        public FakeTopic For(ServiceBusConnection connection, string topic)
        {
            Connection = connection;
            Name = topic;
            return this;
        }

        public Task<IReadOnlyList<string>?> SubscriptionsAsync(CancellationToken cancellationToken) => Call(nameof(SubscriptionsAsync), () => Subscriptions);

        public Task<bool> SubscriptionExistsAsync(string subscription, CancellationToken cancellationToken) => Call(nameof(SubscriptionExistsAsync), () => Existing.Contains(subscription));

        public Task CreateProbeAsync(string subscription, string sender, CancellationToken cancellationToken) =>
            Call(nameof(CreateProbeAsync), () =>
            {
                Probes.Add((subscription, sender));
                return true;
            });

        public Task<int> DrainAsync(string subscription, TimeSpan quiet, TimeSpan max, CancellationToken cancellationToken) => Call(nameof(DrainAsync), () => Messages);

        public Task DeleteSubscriptionAsync(string subscription, CancellationToken cancellationToken)
        {
            Calls.Add(nameof(DeleteSubscriptionAsync));
            Deleted.Add(subscription);
            return Task.CompletedTask;
        }

        private Task<T> Call<T>(string name, Func<T> answer)
        {
            Calls.Add(name);
            return Fail is null ? Task.FromResult(answer()) : Task.FromException<T>(Fail);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Production's container: there, with the blobs a test puts in it.</summary>
    private sealed class FakeContainer : IMediaContainer
    {
        public string Name { get; private set; } = "";

        public bool Exists { get; set; } = true;

        public Dictionary<string, long> Blobs { get; } = [];

        public OptiCliException? Fail { get; set; }

        public FakeContainer For(BlobConnection connection, string container)
        {
            Name = container;
            return this;
        }

        public Task<bool> ExistsAsync(CancellationToken cancellationToken) => Fail is null ? Task.FromResult(Exists) : Task.FromException<bool>(Fail);

        public Task<long?> SizeAsync(string blob, CancellationToken cancellationToken) =>
            Fail is null ? Task.FromResult(Blobs.TryGetValue(blob, out var size) ? size : (long?)null) : Task.FromException<long?>(Fail);
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("opticli-providers-").FullName;

        public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

        public void Write(string relative, string text) => File.WriteAllText(Combine(relative), text);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
