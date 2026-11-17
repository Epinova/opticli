namespace OptiCli.Protocol;

/// <summary>
/// For <c>opticli schema</c>: the values a string output property takes, named by <see cref="Values"/> (or
/// <see cref="TypeName"/>): a class of string constants (its public <c>const string</c> fields, as
/// <see cref="JobStatuses"/>), or an enum (its names as JSON writes them, as <c>VersionStatus</c>). The schema describes
/// the property with them (the npm package types it as their union); nothing else reads it, so it changes no type, no
/// JSON and no message between the CLI and the agent.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OutputValuesAttribute : Attribute
{
    /// <param name="values">The constants class or enum.</param>
    public OutputValuesAttribute(Type values) => Values = values;

    /// <summary>
    /// Names the value set by its assembly-qualified type name, for a Protocol type whose values are defined where
    /// Protocol can't reference them (Core's <c>VersionStatus</c>, which the agent doesn't compile): <c>opticli schema</c>
    /// loads the type, nothing else does.
    /// </summary>
    /// <param name="typeName">The constants class's or enum's assembly-qualified name.</param>
    public OutputValuesAttribute(string typeName) => TypeName = typeName;

    /// <summary>The constants class or enum; null when it is named by <see cref="TypeName"/>.</summary>
    public Type? Values { get; }

    /// <summary>The constants class's or enum's assembly-qualified name, when <see cref="Values"/> is null.</summary>
    public string? TypeName { get; }

    /// <summary>
    /// The CMS defines the values and may add one (a project's status), or the property passes on what the site said:
    /// another string is possible, and the schema allows it (in TypeScript <c>| (string &amp; {})</c>).
    /// </summary>
    public bool Open { get; init; }
}

/// <summary>
/// The name <c>opticli schema</c> gives a constants class used with <see cref="OutputValuesAttribute"/>, where the class's
/// own name is a plural (<c>JobStatuses</c> is the value set <c>JobStatus</c>). Once published, it doesn't change.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ValueSetNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
