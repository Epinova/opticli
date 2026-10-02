using OptiCli.Protocol;

namespace OptiCli.Agent.Drift;

/// <summary>
/// What the database stores about a content type that came from code: its name, class, kind and GUID. Display names,
/// sort order, availability and the like aren't stored for a model (the CMS stores them as unset and fills them in from
/// whichever code runs), so they can't drift.
/// </summary>
/// <param name="ModelType">The class's full name and assembly name, without the assembly version.</param>
internal sealed record TypeSettings(string Name, string? ModelType = null, string? Base = null, Guid? Guid = null);

/// <summary>
/// What the database stores about a property that came from code: its name, type and whether it is culture-specific.
/// Required, order, tab, display name and the like are filled in from whichever code runs, as for content types.
/// </summary>
/// <param name="Type">The property's type in the CMS (its definition type's name, e.g. <c>XhtmlString</c>); from the code, null with <paramref name="TypeId"/> when the CMS can't resolve it.</param>
/// <param name="TypeId">The definition type's id, which tells block types apart; compared when both sides have one.</param>
/// <param name="CultureSpecific">From the code: false without the attribute.</param>
/// <param name="CultureSpecificByAdmin">In the database: culture-specific was set in admin mode, which wins over any code, so it isn't compared.</param>
internal sealed record PropertySettings(string Name, string? Type = null, int? TypeId = null, bool? CultureSpecific = null, bool CultureSpecificByAdmin = false);

/// <summary>A property in the code, with the definition the CMS's analysis matched it to.</summary>
/// <param name="Database">Null: the database has no such property.</param>
/// <param name="RenamedFrom">The definition was found by the old name a migration step gives.</param>
internal sealed record PropertyMatch(PropertySettings Code, PropertySettings? Database, string? RenamedFrom = null);

/// <summary>A content type in the code, with the content type the CMS's analysis matched it to.</summary>
/// <param name="Database">Null: the database has no such content type.</param>
/// <param name="NewerVersion">
/// The database was synced from a newer version of the model's assembly than the build's (the CMS then ignores the
/// model's changes): that version.
/// </param>
/// <param name="PropertiesOnlyInDatabase">Properties the sync made from code (not added in admin mode) that the model no longer has.</param>
internal sealed record TypeMatch(
    TypeSettings Code,
    TypeSettings? Database,
    IReadOnlyList<PropertyMatch> Properties,
    IReadOnlyList<string> PropertiesOnlyInDatabase,
    string? RenamedFrom = null,
    string? NewerVersion = null);

/// <summary>Turns the CMS's analysis of the models against the database into drift items.</summary>
internal static class ContentModelComparison
{
    public const string OnlyInCode = "only in the code";

    public const string OnlyInDatabase = "only in the database";

    /// <param name="typesOnlyInDatabase">Content types the sync made from code that no model in the build matches.</param>
    public static (List<DriftItem> ContentTypes, List<DriftItem> Properties) Items(IEnumerable<TypeMatch> matches, IEnumerable<string> typesOnlyInDatabase)
    {
        var types = new List<DriftItem>();
        var properties = new List<DriftItem>();
        foreach (var match in matches)
        {
            var name = match.Code.Name;
            if (match.Database is not { } database)
            {
                types.Add(new DriftItem(name, DriftAhead.Local, OnlyInCode));
                continue;
            }
            if (match.NewerVersion is { } version)
            {
                types.Add(new DriftItem(name, DriftAhead.Database,
                    $"synced from version {version} of its assembly, newer than this build's, so the CMS ignores the model's changes"));
                continue;
            }
            if (match.RenamedFrom is { } old)
            {
                types.Add(new DriftItem(name, DriftAhead.Local, $"still named {old} in the database (a migration step renames it)"));
            }
            else if (Of(match.Code, database) is { Count: > 0 } differences)
            {
                types.Add(new DriftItem(name, DriftAhead.Unknown, string.Join("; ", differences)));
            }

            foreach (var property in match.Properties)
            {
                var path = $"{name}.{property.Code.Name}";
                if (property.Database is not { } stored)
                {
                    properties.Add(new DriftItem(path, DriftAhead.Local, OnlyInCode));
                }
                else if (property.RenamedFrom is { } oldName)
                {
                    properties.Add(new DriftItem(path, DriftAhead.Local, $"still named {oldName} in the database (a migration step renames it)"));
                }
                else if (Of(property.Code, stored) is { Count: > 0 } differences)
                {
                    properties.Add(new DriftItem(path, DriftAhead.Unknown, string.Join("; ", differences)));
                }
            }
            properties.AddRange(match.PropertiesOnlyInDatabase.Select(p => new DriftItem($"{name}.{p}", DriftAhead.Database, OnlyInDatabase)));
        }
        types.AddRange(typesOnlyInDatabase.Select(t => new DriftItem(t, DriftAhead.Database, OnlyInDatabase)));
        return (types, properties);
    }

    /// <summary>What the code declares differently from what the database stores.</summary>
    public static IReadOnlyList<string> Of(TypeSettings code, TypeSettings database)
    {
        var differences = new List<string>();
        Text(differences, "name", code.Name, database.Name);
        Text(differences, "class", code.ModelType, database.ModelType);
        Text(differences, "kind", code.Base, database.Base);
        Text(differences, "GUID", code.Guid?.ToString("D"), database.Guid?.ToString("D"));
        return differences;
    }

    /// <inheritdoc cref="Of(TypeSettings, TypeSettings)"/>
    public static IReadOnlyList<string> Of(PropertySettings code, PropertySettings database)
    {
        var differences = new List<string>();
        Text(differences, "name", code.Name, database.Name, StringComparison.OrdinalIgnoreCase);
        if (code.Type is null && code.TypeId is null)
        {
            differences.Add($"type: the CMS can't resolve the property's type in this build (its class may have moved, been renamed or be missing), {database.Type ?? "unknown"} in the database");
        }
        else if (code.TypeId is { } codeType && database.TypeId is { } storedType ? codeType != storedType : !string.Equals(code.Type, database.Type, StringComparison.Ordinal))
        {
            differences.Add($"type: {code.Type ?? "unknown"} in the code, {database.Type ?? "unknown"} in the database");
        }
        if (!database.CultureSpecificByAdmin)
        {
            Flag(differences, "culture-specific", code.CultureSpecific ?? false, database.CultureSpecific);
        }
        return differences;
    }

    private static void Text(List<string> differences, string what, string? code, string? database, StringComparison comparison = StringComparison.Ordinal)
    {
        if (!string.IsNullOrEmpty(code) && !string.Equals(code, database, comparison))
        {
            differences.Add($"{what}: {code} in the code, {(string.IsNullOrEmpty(database) ? "none" : database)} in the database");
        }
    }

    private static void Flag(List<string> differences, string what, bool? code, bool? database)
    {
        if (code is { } value && value != (database ?? false))
        {
            differences.Add(value ? $"{what} in the code, not in the database" : $"{what} in the database, not in the code");
        }
    }
}
