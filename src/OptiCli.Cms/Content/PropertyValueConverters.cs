using System.Text.Json;
using EPiServer.Core;

namespace OptiCli.Cms.Content;

/// <summary>
/// A property type only an extension of the site agent knows (an add-on's list of items): how a write sets it from JSON,
/// and how reads and the before/after of a write show it. Registered by the extension when it starts; with none, every
/// property is written and shown as before.
/// </summary>
internal interface IPropertyValueConverter
{
    /// <summary>Whether this converter is for the property.</summary>
    bool Handles(PropertyData property);

    /// <summary>The property's value from what a write gives (not null: a null clears the property as for any other).</summary>
    /// <param name="resolve">Resolves a ref (id, GUID, provider ref) to content the caller may read, as every ref in a write is.</param>
    object? FromJson(PropertyData property, JsonElement value, Func<string, ContentReference> resolve);

    /// <summary>The value as reads and diffs show it (lists, strings, numbers); null for a value this converter isn't for.</summary>
    object? Show(object value);
}

internal static class PropertyValueConverters
{
    private static IReadOnlyList<IPropertyValueConverter> _all = [];

    public static void Register(IPropertyValueConverter converter) => _all = [.. _all, converter];

    /// <summary>The converter for a property; null for every property no extension knows.</summary>
    public static IPropertyValueConverter? For(PropertyData property) => _all.FirstOrDefault(c => c.Handles(property));

    /// <summary>A value as a converter shows it; null when none is for it.</summary>
    public static object? Show(object? value) =>
        value is null || _all.Count == 0 ? null : _all.Select(c => c.Show(value)).FirstOrDefault(shown => shown is not null);

    /// <summary>For a test: takes the converter out again when disposed.</summary>
    internal static IDisposable Use(IPropertyValueConverter converter)
    {
        Register(converter);
        return new Removal(converter);
    }

    private sealed class Removal(IPropertyValueConverter converter) : IDisposable
    {
        public void Dispose() => _all = _all.Where(c => !ReferenceEquals(c, converter)).ToList();
    }
}
