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
    /// Keeps CMS 13 from raising the database's compatibility level on start (<c>DataAccessOptions.UpdateDatabaseCompatibilityLevel</c>,
    /// new in CMS 13): shared mode changes nothing in the database's schema. CMS 12 has no such setting.
    /// </summary>
    public static void TurnOffCompatibilityLevelUpdate(EPiServer.Data.DataAccessOptions options)
    {
#if CMS13
        options.UpdateDatabaseCompatibilityLevel = false;
#else
        _ = options;
#endif
    }

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
    /// CMS 13: a type with neither a class nor a model-sync version on record, of no external content source, and whose
    /// GUID and name no model of the running site has (<paramref name="inBuild"/>). Admin mode makes such types, and so does
    /// a content import that overwrites a code type (it leaves <c>Version</c> unset; every Alloy-template site's types): the
    /// database can't tell which. Never on CMS 12, which records the class of every code type.
    /// </summary>
    /// <param name="inBuild">Whether a model of the running site has the type's GUID or name.</param>
    public static bool OriginUnknown(ContentType type, Func<ContentType, bool> inBuild)
    {
#if CMS13
        return string.IsNullOrEmpty(type.Source) && string.IsNullOrEmpty(type.ModelTypeString) && type.Version is null && !inBuild(type);
#else
        _ = (type, inBuild);
        return false;
#endif
    }

    /// <summary>
    /// Whether a model of the running site has the type's GUID or name, for <see cref="OriginUnknown"/>: the CMS fills a
    /// type's class from the model it matches, so this only matters when that match failed.
    /// </summary>
    public static Func<ContentType, bool> InBuild(IServiceProvider services)
    {
        var models = ContentTypeModels(services).ToList();
        var guids = models.Select(m => m.Guid).Where(g => g != Guid.Empty).ToHashSet();
        var names = models.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return type => guids.Contains(type.GUID) || names.Contains(type.Name);
    }

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
    /// The block type a block property of the site's model holds, by name, where the property's type doesn't tell block
    /// types apart: CMS 13's one generic <c>Block</c> type (the block type is the model's item type). Null for other
    /// properties, and on CMS 12, whose block properties have a property type per block type.
    /// </summary>
    public static string? ModelBlockType(PropertyDefinitionModel model, PropertyDefinitionType? type, IContentTypeRepository types)
    {
#if CMS13
        // The class, or a list's item class (as the CMS's internal ItemType has it).
        var item = model.Type is { IsGenericType: true } list && list.GetGenericArguments() is [var argument] && typeof(IEnumerable<>).MakeGenericType(argument).IsAssignableFrom(list)
            ? argument
            : model.Type;
        return type?.DataType == PropertyDataType.Block && item is not null ? types.Load(item)?.Name ?? item.Name : null;
#else
        _ = (model, type, types);
        return null;
#endif
    }

    /// <summary>The block type a stored block property holds, by name, where <see cref="ModelBlockType"/> gives one: CMS 13.</summary>
    public static string? StoredBlockType(PropertyDefinition property, IContentTypeRepository types) =>
        CmsMajor >= 13 && BlockTypeGuid(property) is { } guid ? types.Load(guid)?.Name ?? guid.ToString() : null;

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
