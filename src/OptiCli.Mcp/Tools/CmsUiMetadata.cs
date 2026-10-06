using EPiServer.Core;
using EPiServer.Shell.ObjectEditing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OptiCli.Cms.Content;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// The CMS edit UI's own metadata for a content item (<see cref="IEditUiMetadata"/>), built as the edit UI builds it: with
/// its <see cref="ExtensibleMetadataProvider"/>, for <see cref="ContentData"/> with the item as the model (what the edit
/// UI's metadata store asks for), so the metadata extenders a site registers for content run, and the editor descriptors
/// of each property; they see the editor the request runs as. A local block's properties are the nested metadata of the
/// property that holds it, as in the edit UI's form. A property it doesn't show for editing is hidden; one it shows
/// read-only is locked.
/// </summary>
/// <remarks>
/// Should the metadata fail to build (a site's editor descriptor that expects the edit UI's own request, say), the
/// failure is logged and null returned: the model's own settings then decide what is shown, and nothing of the item may
/// be changed (<see cref="EditUiProperties"/>).
/// </remarks>
internal sealed class CmsUiMetadata(IServiceProvider services, ILogger<CmsUiMetadata> logger) : IEditUiMetadata
{
    public IReadOnlyDictionary<IContentData, IReadOnlyDictionary<string, PropertyAccess>>? Restricted(IContentData owner)
    {
        if (services.GetService<ExtensibleMetadataProvider>() is not { } provider)
        {
            return null;
        }
        try
        {
            var all = new Dictionary<IContentData, IReadOnlyDictionary<string, PropertyAccess>>(ReferenceEqualityComparer.Instance);
            Walk(provider.GetExtendedMetadataForType(typeof(ContentData), () => owner).Properties, owner, all);
            return all;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "The CMS edit UI's metadata for {Type} couldn't be built; an editor's assistant sees what the model's own settings show of it, and may change none of it.",
                EPiServer.RuntimeModelExtensions.GetOriginalType(owner).FullName);
            return null;
        }
    }

    /// <summary>
    /// The restrictions of <paramref name="owner"/>'s properties, and of the local blocks among them, into
    /// <paramref name="all"/>. A block whose nested metadata fails is left out, and is then asked for on its own.
    /// </summary>
    private void Walk(IEnumerable<ExtendedMetadata> properties, IContentData owner, Dictionary<IContentData, IReadOnlyDictionary<string, PropertyAccess>> all)
    {
        var restricted = new Dictionary<string, PropertyAccess>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (property.PropertyName is not { Length: > 0 } name)
            {
                continue;
            }
            if (!property.ShowForEdit)
            {
                restricted[name] = PropertyAccess.Hidden;
            }
            else if (property.IsReadOnly)
            {
                restricted[name] = PropertyAccess.ReadOnly;
            }
            if (owner.Property[name]?.Value is BlockData block and not IContent && !all.ContainsKey(block))
            {
                try
                {
                    Walk(property.Properties, block, all);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "The CMS edit UI's metadata for the block in {Property} couldn't be built from its content's.", name);
                }
            }
        }
        all[owner] = restricted;
    }
}
