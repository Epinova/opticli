using System.Reflection;
using System.Runtime.Loader;
using OptiCli.Agent.Hosting;

/// <summary>
/// Entry point for <c>DOTNET_STARTUP_HOOKS=/full/path/OptiCli.Agent.dll</c>. Runs before the site's Main.
/// </summary>
/// <remarks>
/// ASPNETCORE_HOSTINGSTARTUPASSEMBLIES only takes an assembly <em>name</em>, and the default load
/// context only resolves names listed in the site's deps.json. The startup hook takes a full path
/// instead, so it makes this assembly resolvable by name and then registers it as a hosting startup.
/// The runtime requires this exact shape: a type named StartupHook in no namespace with a static
/// Initialize method.
/// </remarks>
internal class StartupHook
{
    private const string HostingStartupVariable = "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES";
    private const string PreventHostingStartupVariable = "ASPNETCORE_PREVENTHOSTINGSTARTUP";

    private static int _initialized;

    public static void Initialize()
    {
        // The runtime calls it once per listed hook: the same agent may be named both in DOTNET_STARTUP_HOOKS (serve) and
        // in the build's runtimeconfig (opticli ide on).
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }
        if (!IsAspNetCoreHost())
        {
            // The variable reaches every .NET process started from a shell that exported it (opticli env),
            // including opticli itself and build tools; only a web host has anything for the agent to do.
            return;
        }

        var self = typeof(StartupHook).Assembly;
        var selfName = self.GetName().Name!;

        // Answer for our own name only; everything else stays the host's business.
        AssemblyLoadContext.Default.Resolving += (_, name) =>
            string.Equals(name.Name, selfName, StringComparison.Ordinal) ? self : null;

        if (StdinSecrets.Expected)
        {
            // Before anything reads the environment: the sealed apply gives the token and the connection string here.
            StdinSecrets.Read(Console.In);
        }
        var settings = AgentSettings.FromEnvironment();
        if (settings.PinnedConnection is not null
            && string.Equals(Environment.GetEnvironmentVariable(PreventHostingStartupVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            // Without the hosting startup there is no pin and no guard: fail closed.
            throw new InvalidOperationException(
                $"[opticli] {PreventHostingStartupVariable}=true would stop the agent from pinning the database. Unset it to run with opticli.");
        }

        var list = HostingStartupList.Append(Environment.GetEnvironmentVariable(HostingStartupVariable), selfName);
        Environment.SetEnvironmentVariable(HostingStartupVariable, list);
        // In IDE mode a site that turns hosting startups off just runs without the agent: that run is the developer's.
        if (!settings.Ide && (settings.PinnedConnection is not null || settings.SharedDatabase))
        {
            HostingStartupCheck.Install();
        }

        var version = self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Console.Error.WriteLine($"[opticli] startup hook loaded ({version}{(settings.Ide ? ", IDE mode" : "")}); hosting startups: {list}");
    }

    private static bool IsAspNetCoreHost()
    {
        try
        {
            return Type.GetType("Microsoft.AspNetCore.Hosting.IWebHostBuilder, Microsoft.AspNetCore.Hosting.Abstractions", throwOnError: false) is not null;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException)
        {
            return false;
        }
    }
}
