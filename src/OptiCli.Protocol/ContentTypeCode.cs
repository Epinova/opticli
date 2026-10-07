namespace OptiCli.Protocol;

/// <summary>
/// Response of <see cref="AgentRoutes.TypesWithoutCode"/>: content types defined in code (a model type in
/// <c>tblContentType.ModelType</c>; on CMS 13, which records no class for a model with a GUID, a type its model sync made)
/// whose class the running site can't load: their code was removed (or the package that had it), and the CMS kept the
/// type because content still uses it.
/// </summary>
public sealed record TypesWithoutCodeResult(IReadOnlyList<TypeWithoutCode> Types);

/// <param name="ModelType">The class the CMS has on record, as it stores it; null for a CMS 13 type with a GUID (none recorded).</param>
public sealed record TypeWithoutCode(int Id, Guid Guid, string Name, string? ModelType);
