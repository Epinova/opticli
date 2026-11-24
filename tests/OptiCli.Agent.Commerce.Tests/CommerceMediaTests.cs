using System.Text.Json;
using EPiServer.Commerce.SpecializedProperties;
using EPiServer.Core;
using OptiCli.Agent.Commerce.Catalog;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Tests;

/// <summary>CommerceMediaCollection as a write gives it and a read shows it, apart from the CMS.</summary>
public class CommerceMediaTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Refs_and_objects_are_items_whose_group_and_order_default_to_default_and_their_place()
    {
        var items = CommerceMediaConverter.Items("CommerceMediaCollection", Json("""
            ["12", 13, {"ref": "e56f85d0-e833-4e02-976a-2d11fe4d598c"}, {"ref": "14", "groupName": "swatch", "sortOrder": 7}]
            """));

        Assert.Equal(
        [
            new CommerceMediaConverter.Item("12", "default", 0),
            new CommerceMediaConverter.Item("13", "default", 1),
            new CommerceMediaConverter.Item("e56f85d0-e833-4e02-976a-2d11fe4d598c", "default", 2),
            new CommerceMediaConverter.Item("14", "swatch", 7),
        ], items);
        Assert.Empty(CommerceMediaConverter.Items("CommerceMediaCollection", Json("[]")));
    }

    [Theory]
    [InlineData("\"12\"")]
    [InlineData("{\"ref\": \"12\"}")]
    [InlineData("[true]")]
    [InlineData("[{\"groupName\": \"default\"}]")]
    [InlineData("[\"\"]")]
    public void What_isnt_an_array_of_media_refs_is_usage(string json)
    {
        var error = Assert.Throws<AgentException>(() => CommerceMediaConverter.Items("CommerceMediaCollection", Json(json)));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
        Assert.Contains("'CommerceMediaCollection'", error.Message);
    }

    [Fact]
    public void A_read_shows_refs_groups_and_order_by_sort_order_and_only_for_media_lists()
    {
        var converter = new CommerceMediaConverter();
        var media = new[]
        {
            new CommerceMedia(new ContentReference(14, 3), CommerceMediaConverter.MediaAssetType, "swatch", 2),
            new CommerceMedia(new ContentReference(12), CommerceMediaConverter.MediaAssetType, "default", 0),
        };

        var shown = Assert.IsType<List<Dictionary<string, object?>>>(converter.Show(media));

        Assert.Equal(["12", "14"], shown.Select(m => m["ref"]));
        Assert.Equal(["default", "swatch"], shown.Select(m => m["groupName"]));
        Assert.Equal([0, 2], shown.Select(m => m["sortOrder"]));
        Assert.Null(converter.Show("text"));
        Assert.True(converter.Handles(new PropertyCommerceMediaCollection()));
        Assert.False(converter.Handles(new PropertyLongString()));
    }
}
