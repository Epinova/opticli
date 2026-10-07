namespace OptiCli.Protocol;

/// <summary>
/// Response of <see cref="AgentRoutes.TypesWithoutCode"/>: content types defined in code (a model type in
/// <c>tblContentType.ModelType</c>; on CMS 13, which records no class for a model with a GUID, a type its model sync made)
/// whose class the running site can't load: their code was removed (or the package that had it), and the CMS kept the
/// type because content still uses it. On CMS 13 also the types of unknown origin (<see cref="TypeWithoutCode.OriginUnknown"/>).
/// </summary>
public sealed record TypesWithoutCodeResult(IReadOnlyList<TypeWithoutCode> Types);

/// <param name="ModelType">The class the CMS has on record, as it stores it; null for a CMS 13 type with a GUID (none recorded).</param>
public sealed record TypeWithoutCode(int Id, Guid Guid, string Name, string? ModelType)
{
    /// <summary>
    /// CMS 13: true for a type with neither a class nor a model-sync version on record (<c>tblContentType.Version</c>) whose
    /// GUID no class of the site has. Admin mode makes such types, and so does a content import that overwrites a code type
    /// (every Alloy-template site's types): the database can't tell them apart. Null otherwise.
    /// </summary>
    public bool? OriginUnknown { get; init; }
}
