using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using OptiCli.Agent.Hosting;

namespace OptiCli.Agent.Tests.Hosting;

/// <summary>
/// The decision only: <see cref="HostingStartupCheck.Install"/> is never called here, since a refusal ends the
/// process (Environment.FailFast).
/// </summary>
[Collection(HostingCollection.Name)]
public class HostingStartupCheckTests
{
    [Fact]
    public void A_web_host_built_without_the_hosting_startup_is_refused()
    {
        using var host = new HostBuilder().ConfigureWebHost(_ => { }).Build();

        Assert.True(HostingStartupCheck.Refuses(configured: false, host));
        Assert.False(HostingStartupCheck.Refuses(configured: true, host));
    }

    [Fact]
    public void A_host_without_a_web_host_is_not_refused()
    {
        // A worker or tool built in the same process (e.g. by the site's own code) never runs hosting startups.
        using var host = new HostBuilder().Build();

        Assert.False(HostingStartupCheck.Refuses(configured: false, host));
    }

    [Fact]
    public void Anything_but_a_host_is_not_refused()
    {
        Assert.False(HostingStartupCheck.Refuses(configured: false, null));
        Assert.False(HostingStartupCheck.Refuses(configured: false, new HostBuilder()));
    }

    [Fact]
    public void The_generic_host_reports_each_built_host_where_the_check_listens()
    {
        var built = new List<object?>();
        using var subscription = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name == HostingStartupCheck.ListenerName)
            {
                listener.Subscribe(new Observer<KeyValuePair<string, object?>>(e =>
                {
                    if (e.Key == HostingStartupCheck.HostBuiltEvent)
                    {
                        lock (built)
                        {
                            built.Add(e.Value);
                        }
                    }
                }));
            }
        }));

        using var host = new HostBuilder().ConfigureWebHost(_ => { }).Build();

        lock (built)
        {
            Assert.Contains(host, built);
        }
        Assert.True(HostingStartupCheck.Refuses(configured: false, host));
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
