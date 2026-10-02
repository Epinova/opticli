using System.Reflection;
using EPiServer.Data.Dynamic;
using EPiServer.Data.Dynamic.Internal;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Protocol;

namespace OptiCli.Agent.Drift;

/// <summary>
/// Dynamic Data Store types (<see cref="EPiServerDataStoreAttribute"/>) whose properties no longer match the stored
/// definition. The CMS would remap such a store when it is first used; in shared mode that is off, so using it fails
/// with a <c>StoreInconsistencyException</c> instead.
/// </summary>
/// <remarks>
/// It checks the way <c>EPiServerDynamicDataStoreFactory</c> does before remapping: the stored definition against the
/// mappings the type would get. A store that doesn't exist yet isn't drift: the site creates it on first use (a write
/// that opticli can't turn off).
/// </remarks>
internal static class StoreDrift
{
    /// <summary>Mismatches shown per store; the rest are counted.</summary>
    private const int ErrorsShown = 2;

    public static List<DriftItem> Compare(IServiceProvider services)
    {
        var factory = services.GetService<DynamicDataStoreFactory>() ?? DynamicDataStoreFactory.Instance;
        var items = new List<DriftItem>();
        foreach (var type in StoreTypes())
        {
            try
            {
                var name = factory.GetStoreNameForType(type);
                if (StoreDefinition.Get(name) is not { } stored
                    || stored.ValidateAgainstMappings(type, Reflector.GetStoreDefinitionParametersForType(type), out var errors))
                {
                    continue;
                }
                var shown = string.Join("; ", errors.Take(ErrorsShown).Select(e => e.Trim()));
                var more = errors.Count > ErrorsShown ? $" (and {errors.Count - ErrorsShown} more)" : "";
                items.Add(new DriftItem(name, DriftAhead.Unknown,
                    $"{type.FullName} doesn't match the stored definition, and the store isn't remapped in shared mode, so the site can't use it: {shown}{more}"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A type that can't be reflected or resolved: the site can't use its store either way.
                Console.Error.WriteLine($"[opticli] drift: couldn't check the store of {type.FullName}: {ex.Message}");
            }
        }
        return items;
    }

    /// <summary>
    /// Every type with <see cref="EPiServerDataStoreAttribute"/> in the loaded assemblies that can have one (EPiServer.Data
    /// itself and those that reference it). The CMS's type scanner isn't enough: it lists only the types its own
    /// scanners ask for, which leaves out a site's plain store classes.
    /// </summary>
    private static IEnumerable<Type> StoreTypes()
    {
        var data = typeof(EPiServerDataStoreAttribute).Assembly;
        var dataName = data.GetName().Name;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || (assembly != data && !assembly.GetReferencedAssemblies().Any(r => r.Name == dataName)))
            {
                continue;
            }
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            foreach (var type in types)
            {
                if (type is { IsAbstract: false, IsGenericTypeDefinition: false } && IsStore(type))
                {
                    yield return type;
                }
            }
        }
    }

    private static bool IsStore(Type type)
    {
        try
        {
            return Attribute.GetCustomAttribute(type, typeof(EPiServerDataStoreAttribute), inherit: false) is not null;
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FormatException)
        {
            return false;
        }
    }
}
