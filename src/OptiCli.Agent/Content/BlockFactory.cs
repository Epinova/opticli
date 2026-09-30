using EPiServer.Construction;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Agent.Http;

namespace OptiCli.Agent.Content;

/// <summary>
/// New block instances for block list properties (<c>IList&lt;SomeBlock&gt;</c>), built the way the CMS builds them:
/// an instance with its property collection populated. A plain <c>new SomeBlock()</c> has no properties, so every value
/// set on it would be lost when the list is saved.
/// </summary>
internal sealed class BlockFactory(IContentDataFactory<BlockData> factory, IContentDataBuilder builder, IContentTypeRepository types)
{
    public BlockData Create(Type modelType)
    {
        var type = types.Load(modelType) ?? throw AgentException.Usage($"{modelType.Name} is not a content type the site has registered.");
        var block = factory.CreateInstance(type);
        builder.AddProperties(block, new BuildingContext(type) { SetPropertyValues = true });
        return block;
    }
}
