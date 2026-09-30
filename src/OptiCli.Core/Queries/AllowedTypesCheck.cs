using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.SourceScan;

namespace OptiCli.Core.Queries;

/// <summary>
/// Whether <c>[AllowedTypes]</c> in the site's code lets a ContentArea or reference property hold a type, for plan
/// steps the CMS can't validate yet because the content doesn't exist. Based on <see cref="AllowedInQuery"/>, so the same
/// caveat applies: rules an editor descriptor adds at runtime aren't visible.
/// </summary>
public sealed class AllowedTypesCheck(CmsModel model, Func<CSharpSourceIndex> index)
{
    private CSharpSourceIndex? _index;

    /// <returns>False when <c>[AllowedTypes]</c> leaves the item type out; null when a type or the property is unknown.</returns>
    public bool? Allows(string ownerType, string property, string itemType)
    {
        if (Find(ownerType) is not { } owner || Find(itemType) is not { } item)
        {
            return null;
        }
        _index ??= index();
        return AllowedInQuery.Allows(model, _index, owner, property, item);
    }

    private ContentTypeInfo? Find(string name)
    {
        try
        {
            return model.RequireType(name);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }
}
