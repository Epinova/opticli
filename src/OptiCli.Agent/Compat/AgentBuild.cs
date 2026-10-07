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
    /// Refuses what this agent doesn't do on CMS 13 yet, before anything is read or changed.
    /// </summary>
    /// <param name="what">What was asked for, as the start of a sentence: "Removing content types and properties".</param>
    /// <exception cref="AgentException"><c>refused</c> on CMS 13.</exception>
    public static void RequireCms12(string what, string hint)
    {
        if (CmsMajor != 12)
        {
            throw AgentException.Refused($"{what} isn't supported on CMS 13 by this opticli yet.", hint);
        }
    }

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
    /// Deletes a property definition. CMS 13 made <c>IPropertyDefinitionRepository.Delete</c> an error (properties go with
    /// <c>IContentTypeRepository.Save</c> of their type); until that is ported the orphan removal refuses on CMS 13 before
    /// it gets here.
    /// </summary>
    public static void DeleteProperty(IPropertyDefinitionRepository properties, PropertyDefinition definition)
    {
#if CMS13
        _ = properties;
        throw AgentException.Refused($"Removing the property '{definition.Name}' isn't supported on CMS 13 by this opticli yet.", RemovalHint);
#else
        properties.Delete(definition);
#endif
    }

    /// <summary>The hint of the CMS 13 refusals of <c>types remove</c>, <c>remove-property</c> and <c>prune</c>.</summary>
    public const string RemovalHint = "Use the CMS's admin mode (Content Types) for now.";
}
