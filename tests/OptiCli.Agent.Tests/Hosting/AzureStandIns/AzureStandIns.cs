using EPiServer.Events;
using EPiServer.Events.Providers;

// Stand-ins for the CMS's Azure providers, with the names and members SealedAzure looks for (see SealedAzureTests).
namespace EPiServer.Azure.Events
{
    public class AzureEventProviderOptions
    {
        public string? ConnectionString { get; set; }

        public string? Endpoint { get; set; } = "prod-bus.servicebus.windows.net";

        public string? TopicName { get; set; }
    }

    public class AzureEventProvider : EventProvider
    {
        // CMS 13 keeps it in a property (CMS 12 in a field `_provider`).
        internal DefaultServiceBusEventProvider ServiceBus { get; } = new();

        public override void SendMessage(EventMessage message)
        {
        }
    }

    public class DefaultServiceBusEventProvider
    {
        private object _sendProcessor = new BufferBlock();

        public string SubscriptionName { get; } = "DEVBOX_1a2b3c4d";

        public bool Completed => _sendProcessor is ActionBlock { Completion.IsCompleted: true };

        public void Connect() => _sendProcessor = new ActionBlock();
    }

    /// <summary>Named as the Dataflow block the provider sends with once connected.</summary>
    public sealed class ActionBlock
    {
        private readonly TaskCompletionSource _done = new();

        public Task Completion => _done.Task;

        public void Complete() => _done.TrySetResult();
    }

    public sealed class BufferBlock
    {
    }
}

namespace EPiServer.Azure.Blobs
{
    public class AzureBlobProviderOptions
    {
        public string? ConnectionString { get; set; }

        public Uri? ServiceUri { get; set; }

        public string? ContainerName { get; set; } = "mysitemedia";
    }

    public class AzureBlobProvider
    {
    }
}
