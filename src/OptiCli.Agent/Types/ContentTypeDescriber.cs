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

namespace OptiCli.Agent.Types;

/// <summary>
/// Builds the runtime model of a content type: the stored definition from <see cref="IContentTypeRepository"/>
/// plus what only the C# model knows, read by reflection from its attributes.
/// </summary>
internal sealed class ContentTypeDescriber(IContentTypeRepository types, ContentTypeAvailabilityService availability)
{
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
                .Select(d => DescribeProperty(d, model?.GetProperty(d.Name, BindingFlags.Public | BindingFlags.Instance)))
                .ToList(),
        };
    }

    private static string KindOf(ContentType type) => type switch
    {
        PageType => "page",
        BlockType => "block",
        _ when type.ModelType is { } model && typeof(MediaData).IsAssignableFrom(model) => "media",
        _ when type.ModelType is { } model && typeof(ContentFolder).IsAssignableFrom(model) => "folder",
        _ => "other",
    };

    private ChildTypeRules ChildRules(ContentType type)
    {
        var setting = availability.GetSetting(type.Name);
        return new ChildTypeRules(setting.Availability.ToString().ToLowerInvariant(), setting.AllowedContentTypeNames.ToList());
    }

    private PropertyModel DescribeProperty(PropertyDefinition definition, PropertyInfo? member)
    {
        var attributes = member?.GetCustomAttributes(inherit: true).OfType<Attribute>().ToList() ?? [];
        var display = attributes.OfType<DisplayAttribute>().FirstOrDefault();
        var allowed = attributes.OfType<AllowedTypesAttribute>().FirstOrDefault();
        var scaffold = attributes.OfType<ScaffoldColumnAttribute>().FirstOrDefault();

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
            Searchable = definition.Searchable,
            Visible = definition.DisplayEditUI && scaffold?.Scaffold != false,
            DefinedInCode = definition.ExistsOnModel,
            DisplayName = NullIfEmpty(display?.GetName() ?? definition.EditCaption),
            Description = NullIfEmpty(display?.GetDescription() ?? definition.HelpText),
            Group = NullIfEmpty(display?.GetGroupName() ?? definition.Tab?.Name),
            Order = display?.GetOrder() ?? definition.FieldOrder,
            UiHint = NullIfEmpty(attributes.OfType<UIHintAttribute>().FirstOrDefault()?.UIHint ?? definition.EditorHint),
            AllowedTypes = allowed is { AllowedTypes.Length: > 0 } ? allowed.AllowedTypes.Select(ContentTypeName).ToList() : null,
            RestrictedTypes = allowed is { RestrictedTypes.Length: > 0 } ? allowed.RestrictedTypes.Select(ContentTypeName).ToList() : null,
            Validation = validation.Count > 0 ? validation : null,
            Attributes = other.Count > 0 ? other : null,
        };
    }

    /// <summary>Content type name when the CLR type is one, else the class or interface name (e.g. <c>BlockData</c>).</summary>
    private string ContentTypeName(Type type) => types.Load(type)?.Name ?? type.Name;

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
