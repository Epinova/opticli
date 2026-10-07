namespace OptiCli.Core.SourceScan;

/// <param name="ContentTypeGuid">GUID from a <c>[ContentType(GUID = "...")]</c>-style attribute on the class, if any.</param>
/// <param name="BodyStart">Index of the opening brace in the file text; the ';' of a body-less <c>class Foo : Bar;</c>.</param>
/// <param name="BodyEnd">Index of the closing brace in the file text; the index after the ';' of a body-less declaration.</param>
public sealed record ClassDeclaration(
    string Name,
    string File,
    int Line,
    string? Namespace,
    IReadOnlyList<string> BaseTypes,
    Guid? ContentTypeGuid,
    int BodyStart,
    int BodyEnd)
{
    /// <summary>
    /// An interface rather than a class: on CMS 13 a content type can be one (a Visual Builder contract). Found as content
    /// types' classes, but never walked as a base class.
    /// </summary>
    public bool IsInterface { get; init; }

    public bool Contains(int index) => index > BodyStart && index < BodyEnd;
}
