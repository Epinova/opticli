namespace OptiCli.Protocol;

/// <summary>
/// Response of <see cref="AgentRoutes.TypesWithoutCode"/>: content types defined in code (a model type in
/// <c>tblContentType.ModelType</c>) whose class the running site can't load: their code was removed (or the package
/// that had it), and the CMS kept the type because content still uses it.
/// </summary>
public sealed record TypesWithoutCodeResult(IReadOnlyList<TypeWithoutCode> Types);

/// <param name="ModelType">The class the CMS has on record, as it stores it.</param>
public sealed record TypeWithoutCode(int Id, Guid Guid, string Name, string ModelType);
