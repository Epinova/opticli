using System.ComponentModel.DataAnnotations;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAnnotations;
using OptiCli.Cms.Content;
using OptiCli.Cms.Types;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// A content type's allowed types: from <c>[AllowedTypes]</c>, and where the CMS UI's metadata is to be had (the MCP
/// module), from an editor descriptor for a property's UIHint.
/// </summary>
public class ContentTypeDescriberTests
{
    /// <summary>A type as the edit UI's metadata names it: by its lowercase full name.</summary>
    private static readonly string Teaser = typeof(TeaserBlock).FullName!.ToLowerInvariant();

    [Fact]
    public void An_editor_descriptors_allowed_types_come_from_the_edit_UIs_metadata()
    {
        var metadata = new Metadata(new Dictionary<string, EditUiTypes>
        {
            // The site's editor descriptor for the hint.
            [nameof(AreasBlock.Bottom)] = new([Teaser, "episerver.core.blockdata"], []),
            // The CMS's own ContentArea descriptor: any content.
            [nameof(AreasBlock.Hinted)] = new(["episerver.core.icontentdata"], []),
            // [AllowedTypes], as the CMS UI copies it into the metadata.
            [nameof(AreasBlock.Main)] = new([Teaser], []),
            // A ContentArea without a hint: the descriptor for every ContentArea is the CMS's own.
            [nameof(AreasBlock.Plain)] = new([Teaser], []),
            [nameof(AreasBlock.Excluding)] = new([], [Teaser, "some.unknown.type"]),
        });

        var described = Describe(metadata).Properties.ToDictionary(p => p.Name);

        Assert.Equal(["TeaserBlock", "BlockData"], described[nameof(AreasBlock.Bottom)].AllowedTypes);
        Assert.Equal("BottomTypes", described[nameof(AreasBlock.Bottom)].UiHint);
        Assert.Null(described[nameof(AreasBlock.Bottom)].RestrictedTypes);
        Assert.Null(described[nameof(AreasBlock.Hinted)].AllowedTypes);
        Assert.Equal(["TeaserBlock"], described[nameof(AreasBlock.Main)].AllowedTypes);
        Assert.Equal(["ContentData"], described[nameof(AreasBlock.Main)].RestrictedTypes);
        Assert.Null(described[nameof(AreasBlock.Plain)].AllowedTypes);
        Assert.Null(described[nameof(AreasBlock.Excluding)].AllowedTypes);
        Assert.Equal(["TeaserBlock", "some.unknown.type"], described[nameof(AreasBlock.Excluding)].RestrictedTypes);
    }

    [Fact]
    public void Without_the_edit_UIs_metadata_only_AllowedTypes_counts()
    {
        foreach (var metadata in new IEditUiMetadata?[] { null, new Metadata(null) })
        {
            var described = Describe(metadata).Properties.ToDictionary(p => p.Name);
            Assert.Null(described[nameof(AreasBlock.Bottom)].AllowedTypes);
            Assert.Equal("BottomTypes", described[nameof(AreasBlock.Bottom)].UiHint);
            Assert.Equal(["TeaserBlock"], described[nameof(AreasBlock.Main)].AllowedTypes);
        }
    }

    private static OptiCli.Protocol.ContentTypeModel Describe(IEditUiMetadata? metadata)
    {
        var areas = new ContentType { ID = 7, Name = nameof(AreasBlock), ModelType = typeof(AreasBlock) };
        foreach (var (name, order) in new[] { nameof(AreasBlock.Main), nameof(AreasBlock.Bottom), nameof(AreasBlock.Hinted), nameof(AreasBlock.Plain), nameof(AreasBlock.Excluding) }.Select((n, i) => (n, i)))
        {
            areas.PropertyDefinitions.Add(new PropertyDefinition { ID = 100 + order, Name = name, FieldOrder = order, ExistsOnModel = true, DisplayEditUI = true });
        }
        var teaser = new ContentType { ID = 8, Name = nameof(TeaserBlock), ModelType = typeof(TeaserBlock) };
        var types = Recorder<IContentTypeRepository>.Create();
        types.Recorder.Answer = (method, args) => method.Name switch
        {
            "List" when args is [] => new[] { areas, teaser },
            "Load" when args is [Type type] => new[] { areas, teaser }.FirstOrDefault(t => t.ModelType == type),
            _ => (object?)null,
        };
        // A block type's children aren't described, so the availability service isn't asked.
        return new ContentTypeDescriber(types.Proxy, null!, editUi: metadata).Describe(areas);
    }

    public class TeaserBlock : BlockData
    {
    }

    public class AreasBlock : BlockData
    {
        [AllowedTypes(AllowedTypes = new[] { typeof(TeaserBlock) }, RestrictedTypes = new[] { typeof(ContentData) })]
        [UIHint("BottomTypes")]
        public virtual ContentArea? Main { get; set; }

        [UIHint("BottomTypes")]
        public virtual ContentArea? Bottom { get; set; }

        [UIHint("Wide")]
        public virtual ContentArea? Hinted { get; set; }

        public virtual ContentArea? Plain { get; set; }

        [UIHint("Excluding")]
        public virtual ContentArea? Excluding { get; set; }
    }

    /// <summary>The CMS UI's metadata, faked: these types for any content type, or none to be had (null).</summary>
    private sealed class Metadata(Dictionary<string, EditUiTypes>? types) : IEditUiMetadata
    {
        public IReadOnlyDictionary<IContentData, IReadOnlyDictionary<string, PropertyAccess>>? Restricted(IContentData owner) => null;

        public IReadOnlyDictionary<string, EditUiTypes>? Types(ContentType type) => types;
    }
}
