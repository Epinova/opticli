using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace OptiCli.Core.Drift;

/// <summary>An EF Core migration in the build: its <c>[Migration("id")]</c> and the <c>[DbContext(typeof(...))]</c> it belongs to.</summary>
/// <param name="Context">The context's full type name; null when the class has no <c>[DbContext]</c>.</param>
/// <param name="Assembly">The file it was found in.</param>
public sealed record EfMigration(string Id, string? Context, string Assembly);

/// <summary>
/// Reads what the site's build output says about its database without loading or running any of it: metadata only, with
/// <see cref="System.Reflection.Metadata"/>.
/// </summary>
public static partial class BuildScanner
{
    private const string MigrationsNamespace = "Microsoft.EntityFrameworkCore.Migrations";
    private const string InfrastructureNamespace = "Microsoft.EntityFrameworkCore.Infrastructure";

    /// <summary>Where <c>MigrationAttribute</c> lives: an assembly with migrations references it.</summary>
    private const string RelationalAssembly = "Microsoft.EntityFrameworkCore.Relational";

    /// <summary>The CMS's own schema version check, in <c>EPiServer.Data.dll</c>.</summary>
    private const string SchemaValidatorNamespace = "EPiServer.Data.SchemaUpdates.Internal";
    private const string SchemaValidatorType = "DatabaseVersionValidator";

    /// <summary>The constant with the schema version the packages need: CMS 12's name, then CMS 13's.</summary>
    private static readonly string[] SchemaVersionFields = ["RequiredDatabaseVersion", "MinimumDatabaseVersion"];

    public const string CmsDataAssembly = "EPiServer.Data.dll";

    /// <summary>Every EF Core migration in the assemblies directly in <paramref name="directory"/> (the site's output folder).</summary>
    public static IReadOnlyList<EfMigration> Migrations(string directory)
    {
        var found = new List<EfMigration>();
        if (!Directory.Exists(directory))
        {
            return found;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            found.AddRange(Read(file, MigrationsIn) ?? []);
        }
        return found;
    }

    /// <summary>
    /// The GUIDs of every content type class in the assemblies directly in <paramref name="directory"/> (the site's output
    /// folder, packages' assemblies included): the <c>GUID</c> of each <c>[ContentType]</c> attribute (or one derived from it,
    /// named <c>...ContentTypeAttribute</c>) on a class or interface. Empty when the folder doesn't exist.
    /// </summary>
    public static IReadOnlySet<Guid> ContentTypeGuids(string directory)
    {
        var found = new HashSet<Guid>();
        if (!Directory.Exists(directory))
        {
            return found;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            found.UnionWith(Read(file, ContentTypeGuidsIn) ?? []);
        }
        return found;
    }

    private static List<Guid>? ContentTypeGuidsIn(MetadataReader reader, string file)
    {
        var found = new List<Guid>();
        foreach (var handle in reader.TypeDefinitions)
        {
            foreach (var attributeHandle in reader.GetTypeDefinition(handle).GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                if (AttributeType(reader, attribute.Constructor).Item2.EndsWith("ContentTypeAttribute", StringComparison.Ordinal)
                    && GuidArgument(reader, attribute) is { } guid)
                {
                    found.Add(guid);
                }
            }
        }
        return found;
    }

    /// <summary>The attribute's <c>GUID</c> named argument, when it is a GUID.</summary>
    private static Guid? GuidArgument(MetadataReader reader, CustomAttribute attribute)
    {
        try
        {
            var value = attribute.DecodeValue(new AttributeTypes());
            return value.NamedArguments.FirstOrDefault(a => a.Name == "GUID").Value is string text && Guid.TryParse(text, out var guid) ? guid : null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // An argument this decoder can't type (an enum of another assembly): look for the GUID in the raw blob.
            var blob = reader.GetBlobBytes(attribute.Value);
            var text = System.Text.Encoding.UTF8.GetString(blob);
            var at = text.IndexOf("GUID", StringComparison.Ordinal);
            return at >= 0 && GuidText().Match(text, at) is { Success: true } match && Guid.TryParse(match.Value, out var guid) ? guid : null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}")]
    private static partial System.Text.RegularExpressions.Regex GuidText();

    /// <summary>Types of attribute arguments, by name: enough to decode an attribute's named arguments.</summary>
    private sealed class AttributeTypes : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);

        public string GetTypeFromSerializedName(string name) => name;

        // An enum's size isn't in the blob: the CMS's attribute enums are ints.
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type) => type is "System.Type" or "Type";
    }

    /// <summary>The CMS schema version the packages in <paramref name="cmsDataDll"/> need; null when it can't be read.</summary>
    public static int? RequiredSchemaVersion(string cmsDataDll) => Read(cmsDataDll, SchemaVersionIn);

    /// <summary>
    /// The assembly version of <paramref name="file"/> (for EPiServer's assemblies, the package version, e.g.
    /// <c>12.21.2.0</c>); null when it can't be read.
    /// </summary>
    public static Version? AssemblyVersion(string file) =>
        Read(file, (reader, _) => reader.IsAssembly ? reader.GetAssemblyDefinition().Version : null);

    private static T? Read<T>(string file, Func<MetadataReader, string, T?> read)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            return pe.HasMetadata ? read(pe.GetMetadataReader(), file) : default;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Not a .NET assembly (a native library), or one that can't be read: nothing to find in it.
            return default;
        }
    }

    private static List<EfMigration>? MigrationsIn(MetadataReader reader, string file)
    {
        var referencesRelational = reader.AssemblyReferences
            .Any(r => reader.GetString(reader.GetAssemblyReference(r).Name).Equals(RelationalAssembly, StringComparison.Ordinal));
        if (!referencesRelational)
        {
            return null;
        }
        var found = new List<EfMigration>();
        foreach (var handle in reader.TypeDefinitions)
        {
            string? id = null;
            string? context = null;
            foreach (var attributeHandle in reader.GetTypeDefinition(handle).GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                switch (AttributeType(reader, attribute.Constructor))
                {
                    case (MigrationsNamespace, "MigrationAttribute"):
                        id = FirstString(reader, attribute.Value);
                        break;
                    case (InfrastructureNamespace, "DbContextAttribute"):
                        // A typeof() argument is stored as the type's (assembly-qualified) name.
                        context = FirstString(reader, attribute.Value)?.Split(',')[0].Trim();
                        break;
                }
            }
            if (!string.IsNullOrEmpty(id))
            {
                found.Add(new EfMigration(id, context, Path.GetFileName(file)));
            }
        }
        return found;
    }

    private static int? SchemaVersionIn(MetadataReader reader, string file)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!reader.StringComparer.Equals(type.Name, SchemaValidatorType) || !reader.StringComparer.Equals(type.Namespace, SchemaValidatorNamespace))
            {
                continue;
            }
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (SchemaVersionFields.Any(name => reader.StringComparer.Equals(field.Name, name)) && field.GetDefaultValue() is { IsNil: false } constantHandle
                    && reader.GetConstant(constantHandle) is { TypeCode: ConstantTypeCode.Int32 } constant)
                {
                    return reader.GetBlobReader(constant.Value).ReadInt32();
                }
            }
        }
        return null;
    }

    /// <summary>The namespace and name of the type an attribute's constructor belongs to.</summary>
    private static (string, string) AttributeType(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle type;
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                type = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                type = reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
                break;
            default:
                return ("", "");
        }
        return type.Kind switch
        {
            HandleKind.TypeReference => reader.GetTypeReference((TypeReferenceHandle)type) is var r ? (reader.GetString(r.Namespace), reader.GetString(r.Name)) : default,
            HandleKind.TypeDefinition => reader.GetTypeDefinition((TypeDefinitionHandle)type) is var d ? (reader.GetString(d.Namespace), reader.GetString(d.Name)) : default,
            _ => ("", ""),
        };
    }

    /// <summary>The first fixed argument of an attribute whose constructor takes a string (or a type) first.</summary>
    private static string? FirstString(MetadataReader reader, BlobHandle value)
    {
        var blob = reader.GetBlobReader(value);
        return blob.Length >= 2 && blob.ReadUInt16() == 1 ? blob.ReadSerializedString() : null;
    }
}
