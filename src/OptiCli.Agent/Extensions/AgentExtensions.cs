using System.Runtime.Loader;
using System.Text.Json;
using OptiCli.Protocol;

namespace OptiCli.Agent.Extensions;

/// <summary>
/// Loads the agent's extensions into a site that has what they are for. Today that is the Commerce agent
/// (<c>OptiCli.Agent.Commerce.dll</c>, built for the same CMS major as this agent and shipped beside it), loaded only when
/// the site's build lists <see cref="CommercePackage"/> in its <c>.deps.json</c>. A site without Commerce loads nothing,
/// and nothing here touches a Commerce type: the extension's assembly does, once it is loaded.
/// </summary>
internal static class AgentExtensions
{
    /// <summary>The package whose presence in the site's build makes it a Commerce site.</summary>
    public const string CommercePackage = "EPiServer.Commerce.Core";

    /// <summary>The Commerce agent's assembly name (and file name, with <c>.dll</c>).</summary>
    public const string CommerceAssembly = "OptiCli.Agent.Commerce";

    /// <summary>The Commerce agent's extension type.</summary>
    public const string CommerceType = "OptiCli.Agent.Commerce.CommerceExtension";

    private static readonly object Gate = new();

    private static IReadOnlyList<AgentExtension> _loaded = [];

    private static bool _attempted;

    /// <summary>The extensions loaded into this site.</summary>
    public static IReadOnlyList<AgentExtension> Loaded => _loaded;

    /// <summary>The site's Commerce and whether the Commerce agent loaded; null for a site without Commerce.</summary>
    public static CommerceStatus? Commerce { get; private set; }

    /// <summary>The extension whose routes start with <paramref name="segment"/>; null when none is loaded.</summary>
    public static AgentExtension? Find(string segment) =>
        _loaded.FirstOrDefault(e => string.Equals(e.Segment, segment, StringComparison.Ordinal));

    /// <summary>
    /// Loads the Commerce agent into a Commerce site, once (the hosting startup can run twice, as the startup hook can).
    /// Failing to load it never stops the site: <see cref="Commerce"/> says why, and the CMS agent works as before.
    /// </summary>
    public static void Load()
    {
        lock (Gate)
        {
            if (_attempted)
            {
                return;
            }
            _attempted = true;
            var version = SiteDependencies.PackageVersion(SiteDependencies.ApplicationDepsFile(), CommercePackage);
            if (version is null)
            {
                return;
            }
            var (extension, status) = LoadCommerce(version, Path.GetDirectoryName(typeof(AgentExtensions).Assembly.Location), Environment.GetEnvironmentVariable(AgentProtocol.CommerceVariable));
            Commerce = status;
            if (extension is not null)
            {
                _loaded = [.. _loaded, extension];
                Console.Error.WriteLine($"[opticli] Commerce agent loaded (the site runs Commerce {version}).");
            }
            else
            {
                Console.Error.WriteLine($"[opticli] Commerce agent not loaded (the site runs Commerce {version}): {status.Error}");
            }
        }
    }

    /// <param name="version">The site's Commerce version.</param>
    /// <param name="folder">Where this agent's assembly is, and so the Commerce agent's.</param>
    /// <param name="switchValue"><see cref="AgentProtocol.CommerceVariable"/>: <c>off</c> turns the Commerce agent off.</param>
    internal static (AgentExtension? Extension, CommerceStatus Status) LoadCommerce(string version, string? folder, string? switchValue)
    {
        if (string.Equals(switchValue?.Trim(), AgentProtocol.CommerceOff, StringComparison.OrdinalIgnoreCase))
        {
            return (null, new CommerceStatus(version, false, $"{AgentProtocol.CommerceVariable}={AgentProtocol.CommerceOff} turned it off."));
        }
        var path = folder is null ? null : Path.Combine(folder, CommerceAssembly + ".dll");
        if (path is null || !File.Exists(path))
        {
            return (null, new CommerceStatus(version, false, $"{CommerceAssembly}.dll isn't next to the agent ({folder ?? "no folder"}); reinstall opticli."));
        }
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            var type = assembly.GetType(CommerceType, throwOnError: true)!;
            var extension = (AgentExtension)Activator.CreateInstance(type, nonPublic: true)!;
            return (extension, new CommerceStatus(version, true));
        }
        catch (Exception ex)
        {
            return (null, new CommerceStatus(version, false, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>Starts every loaded extension, once the site's services are built.</summary>
    public static void Start(IServiceProvider services)
    {
        foreach (var extension in _loaded)
        {
            try
            {
                extension.Start(services);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[opticli] The {extension.Segment} agent didn't start: {ex}");
            }
        }
    }

    /// <summary>Puts an extension in for a test, and takes it out again when the result is disposed.</summary>
    internal static IDisposable Use(AgentExtension extension)
    {
        lock (Gate)
        {
            _loaded = [.. _loaded, extension];
        }
        return new Removal(extension);
    }

    private sealed class Removal(AgentExtension extension) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                _loaded = _loaded.Where(e => !ReferenceEquals(e, extension)).ToList();
            }
        }
    }
}

/// <summary>What the site's build output says about its packages: its <c>.deps.json</c>, which the runtime names.</summary>
internal static class SiteDependencies
{
    /// <summary>The application's own <c>.deps.json</c>: the first of the runtime's <c>APP_CONTEXT_DEPS_FILES</c>.</summary>
    public static string? ApplicationDepsFile() =>
        (AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string)?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    /// <summary>The version of a package in a <c>.deps.json</c>'s libraries; null when it isn't there or the file can't be read.</summary>
    public static string? PackageVersion(string? depsFile, string packageId)
    {
        if (depsFile is null || !File.Exists(depsFile))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(depsFile);
            return PackageVersion(JsonDocument.Parse(stream), packageId);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The version of a package in a parsed <c>.deps.json</c> (keys of <c>libraries</c> are <c>id/version</c>).</summary>
    public static string? PackageVersion(JsonDocument deps, string packageId)
    {
        if (!deps.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var prefix = packageId + "/";
        return libraries.EnumerateObject()
            .Where(l => l.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && l.Value.TryGetProperty("type", out var type) && type.GetString() == "package")
            .Select(l => l.Name[prefix.Length..])
            .FirstOrDefault(v => v.Length > 0);
    }
}
