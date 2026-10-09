using EPiServer.Construction;
using EPiServer.Core;
using EPiServer.DataAbstraction;

namespace OptiCli.Cms.Content;

/// <summary>
/// New block instances for block list properties (<c>IList&lt;SomeBlock&gt;</c>) and inline blocks in ContentAreas, built
/// the way the CMS builds them (its <c>IBlockPropertyFactory</c>, which CMS 12.0 doesn't have, does the same): an instance
/// of the type with its property collection populated and its default values set. A plain <c>new SomeBlock()</c> has no
/// properties, so every value set on it would be lost when the list is saved.
/// </summary>
internal sealed class BlockFactory(IContentDataFactory<BlockData> factory, IContentDataBuilder builder, IContentTypeRepository types)
{
    public BlockData Create(Type modelType)
    {
        var type = types.Load(modelType) ?? throw AgentException.Usage($"{modelType.Name} is not a content type the site has registered.");
        return Create(type);
    }

    public BlockData Create(ContentType type)
    {
        var block = factory.CreateInstance(type);
        builder.AddProperties(block, new BuildingContext(type) { SetPropertyValues = true });
        return block;
    }
}
