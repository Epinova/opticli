using Azure;
using Azure.Core.Pipeline;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Azure;

/// <summary>
/// An Azure Service Bus namespace's SAS connection string (<c>Endpoint=sb://…;SharedAccessKeyName=…;SharedAccessKey=…</c>),
/// typed at a sealed run's terminal. The key is never printed.
/// </summary>
public sealed class ServiceBusConnection
{
    private ServiceBusConnection(string value, Uri endpoint, string keyName)
    {
        Value = value;
        Endpoint = endpoint;
        KeyName = keyName;
    }

    /// <summary>The string as typed, for the site and the Service Bus clients. Never printed or written.</summary>
    internal string Value { get; }

    /// <summary>The namespace's address (<c>https://&lt;namespace&gt;.servicebus.windows.net/</c>).</summary>
    public Uri Endpoint { get; }

    public string KeyName { get; }

    /// <exception cref="UsageException">Not a namespace connection string with a shared access key.</exception>
    public static ServiceBusConnection Parse(string value)
    {
        ServiceBusConnectionStringProperties properties;
        try
        {
            properties = ServiceBusConnectionStringProperties.Parse(value.Trim());
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw NotSas();
        }
        if (ConnectionStrings.Parse(value).ContainsKey("UseDevelopmentEmulator"))
        {
            throw new UsageException("The Service Bus emulator can't be production's event bus.");
        }
        if (!string.IsNullOrEmpty(properties.EntityPath))
        {
            throw new UsageException("That connection string is for one entity (EntityPath): give the namespace's, whose key may manage (the CMS's provider makes its own subscription on the topic).");
        }
        if (properties.Endpoint is null || string.IsNullOrEmpty(properties.SharedAccessKeyName) || string.IsNullOrEmpty(properties.SharedAccessKey))
        {
            throw NotSas();
        }
        return new ServiceBusConnection(value.Trim(), new UriBuilder(Uri.UriSchemeHttps, properties.Endpoint.Host).Uri, properties.SharedAccessKeyName);
    }

    private static UsageException NotSas() => new(
        "That isn't a Service Bus connection string with a shared access key (Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=…;SharedAccessKey=…).",
        "Copy it from the namespace's Shared access policies in the Azure portal (a policy that may manage), or from the site's EPiServerAzureEvents connection string. Microsoft Entra ID (managed identity) connections aren't supported here.");

    public override string ToString() => $"{Endpoint.Host} (key {KeyName}, not shown)";
}

/// <summary>
/// A topic of production's event bus, as a sealed run checks it: whether it exists and who listens, the run's own
/// subscription, and a probe subscription that counts the run's messages. <see cref="ServiceBusTopic"/> is the real one.
/// </summary>
public interface IEventBusTopic : IAsyncDisposable
{
    string Name { get; }

    /// <summary>The topic's subscriptions (production's servers each have one); null when the topic doesn't exist.</summary>
    /// <exception cref="UsageException">The key was refused.</exception>
    /// <exception cref="UnreachableException">The namespace didn't answer.</exception>
    Task<IReadOnlyList<string>?> SubscriptionsAsync(CancellationToken cancellationToken);

    Task<bool> SubscriptionExistsAsync(string subscription, CancellationToken cancellationToken);

    /// <summary>
    /// A subscription that receives only what <paramref name="sender"/> sends (the CMS's provider marks its messages with its
    /// subscription's name as <c>SenderID</c>), deleted by Service Bus after five idle minutes if the run can't.
    /// </summary>
    Task CreateProbeAsync(string subscription, string sender, CancellationToken cancellationToken);

    /// <summary>
    /// Receives (and so removes) the subscription's messages until none came for <paramref name="quiet"/>, or for
    /// <paramref name="max"/> at most.
    /// </summary>
    /// <returns>How many there were.</returns>
    Task<int> DrainAsync(string subscription, TimeSpan quiet, TimeSpan max, CancellationToken cancellationToken);

    Task DeleteSubscriptionAsync(string subscription, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IEventBusTopic"/> with the Service Bus SDK: management over HTTPS (<see cref="ServiceBusAdministrationClient"/>),
/// receiving over AMQP on WebSockets (port 443, as HTTPS), so a firewall that lets the management calls through lets the
/// probe through too.
/// </summary>
public sealed class ServiceBusTopic : IEventBusTopic
{
    private readonly ServiceBusConnection _connection;
    private readonly ServiceBusAdministrationClient _admin;
    private ServiceBusClient? _client;

    /// <param name="handler">For tests: answers the management calls. Null sends over the network.</param>
    public ServiceBusTopic(ServiceBusConnection connection, string topic, HttpMessageHandler? handler = null)
    {
        _connection = connection;
        Name = topic.Trim();
        var options = new ServiceBusAdministrationClientOptions();
        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(30);
        if (handler is not null)
        {
            options.Transport = new HttpClientTransport(handler);
            options.Retry.MaxRetries = 0;
        }
        _admin = new ServiceBusAdministrationClient(connection.Value, options);
    }

    public string Name { get; }

    public Task<IReadOnlyList<string>?> SubscriptionsAsync(CancellationToken cancellationToken) =>
        CallAsync("list the topic's subscriptions", async () =>
        {
            if (!(await _admin.TopicExistsAsync(Name, cancellationToken)).Value)
            {
                return (IReadOnlyList<string>?)null;
            }
            var names = new List<string>();
            await foreach (var subscription in _admin.GetSubscriptionsAsync(Name, cancellationToken))
            {
                names.Add(subscription.SubscriptionName);
            }
            return names;
        });

    public Task<bool> SubscriptionExistsAsync(string subscription, CancellationToken cancellationToken) =>
        CallAsync("read the run's subscription", async () => (await _admin.SubscriptionExistsAsync(Name, subscription, cancellationToken)).Value);

    public Task CreateProbeAsync(string subscription, string sender, CancellationToken cancellationToken) =>
        CallAsync("make a subscription on the topic", async () =>
        {
            var options = new CreateSubscriptionOptions(Name, subscription)
            {
                AutoDeleteOnIdle = TimeSpan.FromMinutes(5),
                DefaultMessageTimeToLive = TimeSpan.FromMinutes(10),
            };
            var rule = new CreateRuleOptions("$Default", new SqlRuleFilter($"SenderID = '{sender.Replace("'", "''", StringComparison.Ordinal)}'"));
            return await _admin.CreateSubscriptionAsync(options, rule, cancellationToken);
        });

    public Task<int> DrainAsync(string subscription, TimeSpan quiet, TimeSpan max, CancellationToken cancellationToken) =>
        CallAsync("receive from the probe subscription", async () =>
        {
            _client ??= new ServiceBusClient(_connection.Value, new ServiceBusClientOptions { TransportType = ServiceBusTransportType.AmqpWebSockets });
            await using var receiver = _client.CreateReceiver(Name, subscription, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
            var count = 0;
            var started = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - started < max)
            {
                var messages = await receiver.ReceiveMessagesAsync(100, quiet, cancellationToken);
                if (messages.Count == 0)
                {
                    break;
                }
                count += messages.Count;
            }
            return count;
        });

    public Task DeleteSubscriptionAsync(string subscription, CancellationToken cancellationToken) =>
        CallAsync("delete the probe subscription", async () =>
        {
            try
            {
                return await _admin.DeleteSubscriptionAsync(Name, subscription, cancellationToken);
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
            {
                return null;
            }
        });

    /// <summary>The SDK's failures as opticli's: a refused key is the user's to fix, anything else didn't answer.</summary>
    private async Task<T> CallAsync<T>(string what, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Refused(what, ex.Message);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw Refused(what, ex.Message);
        }
        catch (Exception ex) when (ex is ServiceBusException or RequestFailedException or HttpRequestException or System.Net.Sockets.SocketException or TimeoutException or AggregateException)
        {
            throw new UnreachableException($"Production's event bus ({_connection.Endpoint.Host}) couldn't {what}: {FirstLine(ex.Message)}",
                "Check the connection string's namespace and that this machine reaches it (a firewall or private endpoint may block it).");
        }
    }

    private UsageException Refused(string what, string detail) =>
        new($"Production's event bus refused the key '{_connection.KeyName}' to {what}: {FirstLine(detail)}", "The key needs Manage, Send and Listen on the namespace (the CMS's provider makes its own subscription).");

    /// <summary>The SDK's messages go on with request details over several lines; the first says what happened.</summary>
    internal static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}

/// <summary><c>key=value;…</c> as Azure's connection strings write them (values may hold <c>=</c>).</summary>
internal static class ConnectionStrings
{
    public static IReadOnlyDictionary<string, string> Parse(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Trim().Length > 0)
            .GroupBy(pair => pair[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
}
