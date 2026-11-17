using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAnnotations;
using OptiCli.Protocol;

namespace OptiCli.Cms.Types;

/// <summary>
/// Builds the runtime model of a content type: the stored definition from <see cref="IContentTypeRepository"/>
/// plus what only the C# model knows, read by reflection from its attributes.
/// </summary>
/// <param name="shown">
/// Which properties to describe, from the definition and the model's property: for an editor those the CMS edit UI may
/// show them as editable (<see cref="Content.EditUiProperties.Shown(PropertyDefinition, PropertyInfo?)"/>); every one
/// when null.
/// </param>
/// <param name="editUi">
/// The CMS UI's own metadata, where the caller has it (the MCP module): the allowed types of a property with a
/// <c>UIHint</c> and no <c>[AllowedTypes]</c> come from it, as an editor descriptor may set them for the hint.
/// </param>
internal sealed class ContentTypeDescriber(
    IContentTypeRepository types,
    ContentTypeAvailabilityService availability,
    Func<PropertyDefinition, PropertyInfo?, bool>? shown = null,
    Content.IEditUiMetadata? editUi = null)
{
    /// <summary>What the edit UI's metadata allows every ContentArea and reference list: any content.</summary>
    private const string AnyContent = "episerver.core.icontentdata";

    /// <summary>Content type names (or class and interface names) by the lowercase full names the edit UI's metadata uses.</summary>
    private Dictionary<string, string>? _namesByFullName;

    /// <summary>Attributes mapped to dedicated fields, so they aren't repeated in <c>attributes</c>.</summary>
    private static readonly HashSet<Type> Mapped =
    [
        typeof(DisplayAttribute), typeof(RequiredAttribute), typeof(CultureSpecificAttribute), typeof(SearchableAttribute),
        typeof(UIHintAttribute), typeof(AllowedTypesAttribute), typeof(ScaffoldColumnAttribute),
    ];

    /// <summary>Plumbing properties every attribute has; never interesting as args.</summary>
    private static readonly HashSet<string> IgnoredArgs = new(StringComparer.Ordinal)
    {
        nameof(Attribute.TypeId), nameof(ValidationAttribute.RequiresValidationContext),
        nameof(ValidationAttribute.ErrorMessageResourceName), nameof(ValidationAttribute.ErrorMessageResourceType),
        "AllowedTypesValidators",
    };

    public ContentTypeModel Describe(ContentType type)
    {
        var model = type.ModelType;
        var editUiTypes = editUi?.Types(type);
        return new ContentTypeModel
        {
            Name = type.Name,
            Guid = type.GUID,
            Id = type.ID,
            Kind = KindOf(type),
            ModelType = model?.FullName,
            DisplayName = NullIfEmpty(type.DisplayName),
            Description = NullIfEmpty(type.Description),
            Group = NullIfEmpty(type.GroupName),
            Order = type.SortOrder,
            AvailableInEditMode = type.IsAvailable,
            Children = type is PageType ? ChildRules(type) : null,
            Properties = type.PropertyDefinitions
                .OrderBy(d => d.FieldOrder)
                .Select(d => (Definition: d, Member: model?.GetProperty(d.Name, BindingFlags.Public | BindingFlags.Instance)))
                .Where(p => shown?.Invoke(p.Definition, p.Member) != false)
                .Select(p => DescribeProperty(p.Definition, p.Member, editUiTypes))
                .ToList(),
        };
    }

    private static string KindOf(ContentType type) => type switch
    {
        PageType => "page",
        _ when Compat.CmsApi.IsBlockType(type) => "block",
        _ when type.ModelType is { } model && typeof(MediaData).IsAssignableFrom(model) => "media",
        _ when type.ModelType is { } model && typeof(ContentFolder).IsAssignableFrom(model) => "folder",
        _ => "other",
    };

    private ChildTypeRules ChildRules(ContentType type)
    {
        var setting = availability.GetSetting(type.Name);
        return new ChildTypeRules(setting.Availability.ToString().ToLowerInvariant(), setting.AllowedContentTypeNames.ToList());
    }

    private PropertyModel DescribeProperty(PropertyDefinition definition, PropertyInfo? member, IReadOnlyDictionary<string, Content.EditUiTypes>? editUiTypes)
    {
        var attributes = member?.GetCustomAttributes(inherit: true).OfType<Attribute>().ToList() ?? [];
        var display = attributes.OfType<DisplayAttribute>().FirstOrDefault();
        var allowed = attributes.OfType<AllowedTypesAttribute>().FirstOrDefault();
        var scaffold = attributes.OfType<ScaffoldColumnAttribute>().FirstOrDefault();
        var uiHint = NullIfEmpty(attributes.OfType<UIHintAttribute>().FirstOrDefault()?.UIHint ?? definition.EditorHint);
        // [AllowedTypes] is what the edit UI uses where a property has it (the CMS UI copies it into the metadata). Without
        // it, an editor descriptor for the property's UIHint may set the types, which only the edit UI's metadata knows.
        var fromEditUi = allowed is null && uiHint is not null ? FromEditUi(editUiTypes?.GetValueOrDefault(definition.Name)) : null;

        var validation = attributes
            .Where(a => a is ValidationAttribute && !Mapped.Contains(a.GetType()))
            .Select(Describe)
            .ToList();
        var other = attributes
            .Where(a => a is not ValidationAttribute && !Mapped.Contains(a.GetType()) && !IsCompilerNoise(a))
            .Select(Describe)
            .ToList();

        return new PropertyModel
        {
            Name = definition.Name,
            ClrType = TypeName(member?.PropertyType ?? definition.Type?.DefinitionType),
            PropertyType = ShortTypeName(definition.Type?.DefinitionType) ?? definition.Type?.Name ?? "unknown",
            Required = definition.Required || attributes.OfType<RequiredAttribute>().Any(),
            CultureSpecific = definition.LanguageSpecific,
            Searchable = Compat.CmsApi.IsSearchable(definition),
            Visible = definition.DisplayEditUI && scaffold?.Scaffold != false,
            DefinedInCode = definition.ExistsOnModel,
            DisplayName = NullIfEmpty(display?.GetName() ?? definition.EditCaption),
            Description = NullIfEmpty(display?.GetDescription() ?? definition.HelpText),
            Group = NullIfEmpty(display?.GetGroupName() ?? definition.Tab?.Name),
            Order = display?.GetOrder() ?? definition.FieldOrder,
            UiHint = uiHint,
            AllowedTypes = allowed is { AllowedTypes.Length: > 0 } ? allowed.AllowedTypes.Select(ContentTypeName).ToList() : fromEditUi?.Allowed,
            RestrictedTypes = allowed is { RestrictedTypes.Length: > 0 } ? allowed.RestrictedTypes.Select(ContentTypeName).ToList() : fromEditUi?.Restricted,
            Validation = validation.Count > 0 ? validation : null,
            Attributes = other.Count > 0 ? other : null,
        };
    }

    /// <summary>Content type name when the CLR type is one, else the class or interface name (e.g. <c>BlockData</c>).</summary>
    private string ContentTypeName(Type type) => types.Load(type)?.Name ?? type.Name;

    /// <summary>
    /// The edit UI's types for a property, named as <c>[AllowedTypes]</c>'s are; null when it has none, or allows any
    /// content and restricts none (the CMS's own default for a ContentArea or reference list).
    /// </summary>
    private (IReadOnlyList<string>? Allowed, IReadOnlyList<string>? Restricted)? FromEditUi(Content.EditUiTypes? metadata)
    {
        if (metadata is null || (metadata.Restricted.Count == 0 && (metadata.Allowed.Count == 0 || metadata.Allowed.All(a => a.Equals(AnyContent, StringComparison.OrdinalIgnoreCase)))))
        {
            return null;
        }
        return (
            metadata.Allowed.Count > 0 ? metadata.Allowed.Select(NameOf).Distinct().ToList() : null,
            metadata.Restricted.Count > 0 ? metadata.Restricted.Select(NameOf).Distinct().ToList() : null);
    }

    /// <summary>
    /// A type the edit UI's metadata names by its lowercase full name: the content type's name, or the name of a class or
    /// interface content types derive from; the full name as given when it is neither.
    /// </summary>
    private string NameOf(string fullName)
    {
        if (_namesByFullName is null)
        {
            _namesByFullName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var models = types.List().Where(t => t.ModelType is not null).ToList();
            foreach (var type in models)
            {
                _namesByFullName.TryAdd(type.ModelType!.FullName ?? type.ModelType.Name, type.Name);
            }
            foreach (var model in models.Select(t => t.ModelType!))
            {
                for (var basis = model.BaseType; basis is not null; basis = basis.BaseType)
                {
                    _namesByFullName.TryAdd(basis.FullName ?? basis.Name, basis.Name);
                }
                foreach (var contract in model.GetInterfaces())
                {
                    _namesByFullName.TryAdd(contract.FullName ?? contract.Name, contract.Name);
                }
            }
        }
        return _namesByFullName.GetValueOrDefault(fullName, fullName);
    }

    private static AttributeModel Describe(Attribute attribute)
    {
        var type = attribute.GetType();
        var args = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IgnoredArgs.Contains(property.Name) || property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }
            try
            {
                if (Format(property.GetValue(attribute)) is { } value)
                {
                    args[JsonName(property.Name)] = value;
                }
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NotSupportedException)
            {
                // Some attributes throw from getters that aren't meant to be read directly; skip those.
            }
        }

        var name = type.Name.EndsWith(nameof(Attribute), StringComparison.Ordinal) ? type.Name[..^nameof(Attribute).Length] : type.Name;
        return new AttributeModel(name, args.Count > 0 ? args : null);
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        string s => s.Length == 0 ? null : s,
        Type t => t.FullName,
        IEnumerable items => string.Join(", ", items.Cast<object?>().Select(Format).Where(v => v is not null)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static bool IsCompilerNoise(Attribute attribute) =>
        attribute.GetType().Namespace == typeof(CompilerGeneratedAttribute).Namespace
        || attribute is EditorBrowsableAttribute;

    /// <summary>C#-style names: <c>System.Collections.Generic.IList&lt;EPiServer.Core.ContentReference&gt;</c>.</summary>
    private static string? TypeName(Type? type) => FormatType(type, fullNames: true);

    /// <summary><c>PropertyBlock&lt;ImageBlock&gt;</c> rather than <c>PropertyBlock`1</c>.</summary>
    private static string? ShortTypeName(Type? type) => FormatType(type, fullNames: false);

    private static string? FormatType(Type? type, bool fullNames)
    {
        if (type is null)
        {
            return null;
        }
        var name = (fullNames ? (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName : null) ?? type.Name;
        if (!type.IsGenericType)
        {
            return name;
        }
        var tick = name.IndexOf('`');
        var root = tick < 0 ? name : name[..tick];
        return $"{root}<{string.Join(", ", type.GetGenericArguments().Select(a => FormatType(a, fullNames)))}>";
    }

    private static string JsonName(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
