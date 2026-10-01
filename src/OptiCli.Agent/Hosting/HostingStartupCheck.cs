using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace OptiCli.Agent.Hosting;

/// <summary>
/// Fails closed when a web host is built without <see cref="AgentHostingStartup"/> having run, e.g. because the site
/// turns hosting startups off in code (<c>UseSetting(WebHostDefaults.PreventHostingStartupKey, "true")</c>), which
/// the startup hook can't see. Without it there is no database pin, no guard and, against a shared database, the
/// scheduler, schema updates and content type sync stay on.
/// </summary>
/// <remarks>
/// Hosting startups run while the web host builder is set up, before <c>Build()</c>; the generic host reports the
/// built host on the "Microsoft.Extensions.Hosting" diagnostic listener as "HostBuilt", before anything starts.
/// </remarks>
internal static class HostingStartupCheck
{
    internal const string ListenerName = "Microsoft.Extensions.Hosting";
    internal const string HostBuiltEvent = "HostBuilt";

    private static int _configured;
    private static IDisposable? _subscription;

    public static void MarkConfigured() => Interlocked.Exchange(ref _configured, 1);

    public static void Install() => _subscription ??= DiagnosticListener.AllListeners.Subscribe(new Listeners());

    /// <summary>A web host (not a generic host without one, e.g. a worker) was built without the hosting startup.</summary>
    internal static bool Refuses(bool configured, object? payload) =>
        !configured && payload is IHost host && host.Services.GetService(typeof(IWebHostEnvironment)) is not null;

    private static void OnHostBuilt(object? payload)
    {
        if (!Refuses(Volatile.Read(ref _configured) == 1, payload))
        {
            return;
        }
        const string message = "[opticli] The site was built without the opticli hosting startup (hosting startups are turned off in its code), so the database pin and safety checks can't run. Refusing to start.";
        Console.Error.WriteLine(message);
        Environment.FailFast(message);
    }

    private sealed class Listeners : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == ListenerName)
            {
                listener.Subscribe(new Events());
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private sealed class Events : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == HostBuiltEvent)
            {
                OnHostBuilt(value.Value);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
