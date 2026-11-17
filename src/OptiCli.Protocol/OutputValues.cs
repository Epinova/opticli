namespace OptiCli.Protocol;

/// <summary>
/// For <c>opticli schema</c>: the values a string output property takes, named by <paramref name="values"/>: a class of
/// string constants (its public <c>const string</c> fields, as <see cref="JobStatuses"/>), or an enum (its names as JSON
/// writes them, as <c>VersionStatus</c>). The schema describes the property with them (the npm package types it as their
/// union); nothing else reads it, so it changes no type, no JSON and no message between the CLI and the agent.
/// </summary>
/// <param name="values">The constants class or enum.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OutputValuesAttribute(Type values) : Attribute
{
    public Type Values { get; } = values;

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
