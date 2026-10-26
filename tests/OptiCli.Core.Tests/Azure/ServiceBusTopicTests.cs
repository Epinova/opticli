using System.Net;
using OptiCli.Core.Azure;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Tests.Azure;

/// <summary>
/// Production's event bus through the Service Bus SDK: the connection string typed at a sealed run, and the management calls
/// (the topic, its subscriptions, the probe) with the SDK's HTTP transport answered here. Receiving goes over AMQP, which
/// only a real namespace answers.
/// </summary>
public sealed class ServiceBusTopicTests
{
    private const string Connection = "Endpoint=sb://prod-bus.servicebus.windows.net/;SharedAccessKeyName=manage;SharedAccessKey=bus-secret";

    private const string Ns = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";

    private static string Subscription(string name) =>
        $"""<entry xmlns="http://www.w3.org/2005/Atom"><title type="text">{name}</title><content type="application/xml"><SubscriptionDescription xmlns="{Ns}" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><LockDuration>PT1M</LockDuration><RequiresSession>false</RequiresSession><DefaultMessageTimeToLive>P14D</DefaultMessageTimeToLive><DeadLetteringOnMessageExpiration>false</DeadLetteringOnMessageExpiration><DeadLetteringOnFilterEvaluationExceptions>false</DeadLetteringOnFilterEvaluationExceptions><MaxDeliveryCount>10</MaxDeliveryCount><EnableBatchedOperations>true</EnableBatchedOperations><Status>Active</Status><AutoDeleteOnIdle>PT30M</AutoDeleteOnIdle></SubscriptionDescription></content></entry>""";

    private static string Feed(params string[] names) =>
        $"""<feed xmlns="http://www.w3.org/2005/Atom"><title type="text">Subscriptions</title>{string.Concat(names.Select(n => Subscription(n).Replace(" xmlns=\"http://www.w3.org/2005/Atom\"", "", StringComparison.Ordinal)))}</feed>""";

    private const string Topic =
        $"""<entry xmlns="http://www.w3.org/2005/Atom"><title type="text">SiteEvents</title><content type="application/xml"><TopicDescription xmlns="{Ns}"><DefaultMessageTimeToLive>P14D</DefaultMessageTimeToLive><MaxSizeInMegabytes>5120</MaxSizeInMegabytes><RequiresDuplicateDetection>false</RequiresDuplicateDetection><EnableBatchedOperations>true</EnableBatchedOperations><Status>Active</Status><SupportOrdering>true</SupportOrdering><AutoDeleteOnIdle>P10675199DT2H48M5.4775807S</AutoDeleteOnIdle><EnablePartitioning>true</EnablePartitioning></TopicDescription></content></entry>""";

    [Fact]
    public void A_namespace_connection_string_with_a_key_is_taken_and_never_printed()
    {
        var connection = ServiceBusConnection.Parse($" {Connection} ");

        Assert.Equal("https://prod-bus.servicebus.windows.net/", connection.Endpoint.AbsoluteUri);
        Assert.Equal("manage", connection.KeyName);
        Assert.DoesNotContain("bus-secret", connection.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Endpoint=sb://prod-bus.servicebus.windows.net/;SharedAccessKeyName=manage;SharedAccessKey=k;EntityPath=siteevents", "one entity")]
    [InlineData("Endpoint=sb://prod-bus.servicebus.windows.net/", "shared access key")]
    [InlineData("Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;", "emulator")]
    [InlineData("prod-bus.servicebus.windows.net", "shared access key")]
    public void Other_forms_are_refused(string value, string expected)
    {
        var refused = Assert.Throws<UsageException>(() => ServiceBusConnection.Parse(value));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SAS_KEY_VALUE", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_topics_subscriptions_are_listed_with_a_shared_access_signature()
    {
        var http = new FakeHttp()
            .On("GET", "/SiteEvents", HttpStatusCode.OK, Topic)
            .On("GET", "/SiteEvents/subscriptions", HttpStatusCode.OK, Feed("WEB0_aaaa1111", "WEB1_bbbb2222"));
        await using var topic = new ServiceBusTopic(ServiceBusConnection.Parse(Connection), "SiteEvents", http);

        var subscriptions = await topic.SubscriptionsAsync(CancellationToken.None);

        Assert.Equal(["WEB0_aaaa1111", "WEB1_bbbb2222"], subscriptions);
        Assert.All(http.Requests, r =>
        {
            Assert.Equal("prod-bus.servicebus.windows.net", r.Request.RequestUri!.Host);
            Assert.StartsWith("SharedAccessSignature sr=", r.Request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
            Assert.Contains("skn=manage", r.Request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_topic_that_isnt_there_has_no_subscriptions_to_list()
    {
        var http = new FakeHttp().On("GET", "/SiteEvents", HttpStatusCode.NotFound);
        await using var topic = new ServiceBusTopic(ServiceBusConnection.Parse(Connection), "SiteEvents", http);

        Assert.Null(await topic.SubscriptionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_key_says_what_it_needs_without_the_key()
    {
        var http = new FakeHttp().On("GET", "/SiteEvents", HttpStatusCode.Unauthorized, "<Error><Code>401</Code><Detail>Manage,EntityRead claims required for this operation.</Detail></Error>");
        await using var topic = new ServiceBusTopic(ServiceBusConnection.Parse(Connection), "SiteEvents", http);

        var refused = await Assert.ThrowsAsync<UsageException>(() => topic.SubscriptionsAsync(CancellationToken.None));

        Assert.Contains("refused the key 'manage'", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bus-secret", refused.Message + refused.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task The_runs_subscription_is_looked_up_by_name(HttpStatusCode status, bool exists)
    {
        var http = new FakeHttp().On("GET", "/SiteEvents/subscriptions/DEVBOX_1a2b3c4d", status, exists ? Subscription("DEVBOX_1a2b3c4d") : "");
        await using var topic = new ServiceBusTopic(ServiceBusConnection.Parse(Connection), "SiteEvents", http);

        Assert.Equal(exists, await topic.SubscriptionExistsAsync("DEVBOX_1a2b3c4d", CancellationToken.None));
    }

    [Fact]
    public async Task The_probe_takes_only_the_runs_messages_and_goes_after_five_idle_minutes()
    {
        var http = new FakeHttp()
            .On("PUT", "/SiteEvents/subscriptions/opticli-probe-1", HttpStatusCode.Created, Subscription("opticli-probe-1"))
            .On("DELETE", "/SiteEvents/subscriptions/opticli-probe-1", HttpStatusCode.OK);
        await using var topic = new ServiceBusTopic(ServiceBusConnection.Parse(Connection), "SiteEvents", http);

        await topic.CreateProbeAsync("opticli-probe-1", "DEVBOX_1a2b'3c4d", CancellationToken.None);
        await topic.DeleteSubscriptionAsync("opticli-probe-1", CancellationToken.None);

        var body = http.Requests[0].Body!;
        Assert.Contains("<SqlExpression>SenderID = 'DEVBOX_1a2b''3c4d'</SqlExpression>", body, StringComparison.Ordinal);
        Assert.Contains("<AutoDeleteOnIdle>PT5M</AutoDeleteOnIdle>", body, StringComparison.Ordinal);
        Assert.Equal("DELETE", http.Requests[^1].Request.Method.Method);
    }
}
