namespace OptiCli.Core.Output;

/// <summary>
/// The JSON Schema of an output property whose C# type can't say what it holds (an <c>object</c> that is
/// <c>true</c>, <c>false</c> or <c>"stale"</c>), for <c>opticli schema</c>. Use it only where a precise type would change
/// the JSON; next to the property, so it changes with it.
/// </summary>
/// <param name="json">The property's schema, as JSON.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OutputSchemaAttribute(string json) : Attribute
{
    public string Json { get; } = json;
}
