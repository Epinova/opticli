namespace OptiCli.Protocol;

/// <summary>
/// Response of <see cref="AgentRoutes.Type"/>: the runtime model of a content type, i.e. what the
/// database doesn't store (validation attributes, allowed types, editor hints).
/// </summary>
public sealed record ContentTypeModel
{
    public required string Name { get; init; }

    public required Guid Guid { get; init; }

    public required int Id { get; init; }

    /// <summary><c>page</c>, <c>block</c>, <c>media</c>, <c>folder</c> or <c>other</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Full name of the C# model class; null for types defined only in admin mode.</summary>
    public string? ModelType { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public string? Group { get; init; }

    public int? Order { get; init; }

    public bool AvailableInEditMode { get; init; }

    /// <summary>Which content types may be created below items of this type (pages only).</summary>
    public ChildTypeRules? Children { get; init; }

    public required IReadOnlyList<PropertyModel> Properties { get; init; }
}

/// <param name="Availability"><c>all</c>, <c>none</c> or <c>specific</c> (then see <paramref name="Allowed"/>).</param>
public sealed record ChildTypeRules(string Availability, IReadOnlyList<string> Allowed);

public sealed record PropertyModel
{
    public required string Name { get; init; }

    /// <summary>C# type of the model property (e.g. <c>EPiServer.Core.ContentArea</c>), or of the property's value for admin-only properties.</summary>
    public string? ClrType { get; init; }

    /// <summary>The CMS PropertyData class that stores it (e.g. <c>PropertyContentArea</c>).</summary>
    public required string PropertyType { get; init; }

    public bool Required { get; init; }

    public bool CultureSpecific { get; init; }

    public bool Searchable { get; init; }

    /// <summary>False when the property is hidden from editors (<c>[ScaffoldColumn(false)]</c> or admin setting).</summary>
    public bool Visible { get; init; }

    /// <summary>True when the property exists on the C# model; false for admin-mode-only properties.</summary>
    public bool DefinedInCode { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>Editor tab.</summary>
    public string? Group { get; init; }

    public int? Order { get; init; }

    public string? UiHint { get; init; }

    /// <summary>From <c>[AllowedTypes]</c>: content type names (or base class/interface names) that may be referenced.</summary>
    public IReadOnlyList<string>? AllowedTypes { get; init; }

    public IReadOnlyList<string>? RestrictedTypes { get; init; }

    /// <summary>Validation attributes (<c>[StringLength]</c>, <c>[Range]</c>, custom ones) with their settings.</summary>
    public IReadOnlyList<AttributeModel>? Validation { get; init; }

    /// <summary>Other attributes that matter to editors, e.g. selection factories or backing types.</summary>
    public IReadOnlyList<AttributeModel>? Attributes { get; init; }
}

/// <param name="Name">Attribute class name without the <c>Attribute</c> suffix.</param>
/// <param name="Args">Public settings of the attribute in invariant string form (types as full names).</param>
public sealed record AttributeModel(string Name, IReadOnlyDictionary<string, string>? Args = null);
