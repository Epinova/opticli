using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAbstraction.RuntimeModel;
#if !CMS13
using EPiServer.DataAbstraction.RuntimeModel.Internal;
#endif
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using ContentTypeModel = EPiServer.DataAbstraction.RuntimeModel.ContentTypeModel;

namespace OptiCli.Agent.Compat;

/// <summary>
/// The CMS major this build of the agent is compiled for, and the developer-only CMS APIs that differ between CMS 12 and
/// 13 (the shared operations' are in <c>OptiCli.Cms/Compat</c>). The net8.0 build is CMS 12's, the net10.0 build
/// (<c>CMS13</c> defined) CMS 13's; the CLI injects the one that matches the site.
/// </summary>
internal static class AgentBuild
{
#if CMS13
    public const int CmsMajor = 13;
#else
    public const int CmsMajor = 12;
#endif

    /// <summary>
    /// Whether the content type came from code (the CMS's model sync made it), whether or not that code is still there.
    /// CMS 12 records the class of every such type (<c>ModelTypeString</c>). CMS 13 records none for a model with a GUID,
    /// only the version of its assembly (<c>Version</c>, which the sync sets and admin mode doesn't); a type of an external
    /// content source (<c>Source</c>) isn't the site's code.
    /// </summary>
    public static bool FromCode(ContentType type) =>
#if CMS13
        string.IsNullOrEmpty(type.Source) && (!string.IsNullOrEmpty(type.ModelTypeString) || type.Version is not null);
#else
        !string.IsNullOrEmpty(type.ModelTypeString);
#endif

    /// <summary>
    /// The version (major.minor) of the assembly the database's type was synced from, which the sync compares with the
    /// model's: CMS 13's <c>Version</c>, else the one in the class on record.
    /// </summary>
    public static string? SyncedVersion(ContentType type) =>
#if CMS13
        type.Version is { } version ? $"{version.Major}.{version.Minor}" : Drift.ContentModelScan.AssemblyVersion(type.ModelTypeString);
#else
        Drift.ContentModelScan.AssemblyVersion(type.ModelTypeString);
#endif

    /// <summary>
    /// The CMS type the content model sync would give a property: <c>PropertyDefinitionSynchronizer.ResolveType</c> on
    /// CMS 12, the public <c>IPropertyDefinitionTypeResolver</c> that replaced it on CMS 13.
    /// </summary>
    public static Func<PropertyDefinitionModel, PropertyDefinitionType?> PropertyTypeResolver(IServiceProvider services)
    {
#if CMS13
        var resolver = services.GetRequiredService<IPropertyDefinitionTypeResolver>();
        return resolver.ResolveType;
#else
        var synchronizer = services.GetRequiredService<PropertyDefinitionSynchronizer>();
        return synchronizer.ResolveType;
#endif
    }

    /// <summary>
    /// The content type models the CMS scanned from the site's code: <c>ContentTypeModelRepository</c> on CMS 12, the
    /// <c>IContentTypeModelRepository</c> that replaced it on CMS 13.
    /// </summary>
    public static IEnumerable<ContentTypeModel> ContentTypeModels(IServiceProvider services) =>
#if CMS13
        services.GetRequiredService<IContentTypeModelRepository>().List();
#else
        services.GetRequiredService<ContentTypeModelRepository>().List();
#endif

    /// <summary>Whether a property definition of a content type has a model in the site's code (the same repositories).</summary>
    public static Func<int, PropertyDefinition, bool> HasPropertyModel(IServiceProvider services)
    {
#if CMS13
        var models = services.GetRequiredService<IContentTypeModelRepository>();
#else
        var models = services.GetRequiredService<ContentTypeModelRepository>();
#endif
        return (contentTypeId, property) => models.GetPropertyModel(contentTypeId, property) is not null;
    }

    /// <summary>
    /// The GUID of the block type a block property holds; null for other properties. CMS 12 has a property type per block
    /// type (<c>BlockPropertyDefinitionType</c>); CMS 13 one generic <c>Block</c> type, with the block type on the
    /// definition (<c>ItemTypeReference</c>).
    /// </summary>
    public static Guid? BlockTypeGuid(PropertyDefinition property) =>
#if CMS13
        property.Type?.DataType == PropertyDataType.Block ? property.ItemTypeReference?.GUID : null;
#else
        (property.Type as BlockPropertyDefinitionType)?.BlockType?.GUID;
#endif

    /// <summary>
    /// Deletes a property definition, and its values with it. CMS 12: <c>IPropertyDefinitionRepository.Delete</c>. CMS 13
    /// made that an error: a property goes when its type is saved without it (<c>IContentTypeRepository.Save</c>, which
    /// deletes the definitions the type no longer lists, through the same delete), so a writable copy of the type is
    /// saved without the property.
    /// </summary>
    /// <exception cref="AgentException"><c>conflict</c>: the type or the property was removed meanwhile.</exception>
    public static void DeleteProperty(IContentTypeRepository types, IPropertyDefinitionRepository properties, PropertyDefinition definition)
    {
#if CMS13
        _ = properties;
        var type = types.Load(definition.ContentTypeID)?.CreateWritableClone() as ContentType
            ?? throw AgentException.Conflict($"The content type of '{definition.Name}' was removed meanwhile.");
        var property = type.PropertyDefinitions.FirstOrDefault(p => p.ID == definition.ID)
            ?? throw AgentException.Conflict($"'{definition.Name}' was removed meanwhile.");
        type.PropertyDefinitions.Remove(property);
        types.Save(type);
#else
        _ = types;
        properties.Delete(definition);
#endif
    }
}
