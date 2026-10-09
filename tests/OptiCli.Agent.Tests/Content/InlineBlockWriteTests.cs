using System.Security.Principal;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using EPiServer.SpecializedProperties;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Tests.Cms;
using OptiCli.Cms;
using OptiCli.Cms.Compat;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

/// <summary>
/// Inline blocks in ContentAreas (<c>ContentAreaItem.InlineBlock</c>, CMS 12.20 and CMS 13), where no CMS is needed. The
/// agent's CMS 12 build compiles against CMS 12.0, which has none: there a write of one is refused with the version it
/// needs; the CMS 13 build runs the rest.
/// </summary>
public class InlineBlockWriteTests
{
    private const int ItemType = 7;

    private static readonly ContentType[] Types =
    [
        new ContentType { ID = ItemType, Name = nameof(PropertyWriterTests.ItemBlock), ModelType = typeof(PropertyWriterTests.ItemBlock) },
        new PageType { ID = 8, Name = "StandardPage", ModelType = typeof(PageData) },
    ];

    private static PropertyWriter Writer(CmsCaller caller = CmsCaller.Developer)
    {
        var types = Recorder<IContentTypeRepository>.Create();
        types.Recorder.Answer = (method, args) => (method.Name, args) switch
        {
            ("Load", [int id]) => Types.FirstOrDefault(t => t.ID == id),
            ("Load", [string name]) => Types.FirstOrDefault(t => t.Name == name),
            ("Load", [Type model]) => Types.FirstOrDefault(t => t.ModelType == model) ?? new ContentType { ID = 99, Name = model.Name, ModelType = model },
            ("List", []) => Types,
            _ => null,
        };
        var factory = Recorder<EPiServer.Construction.IContentDataFactory<BlockData>>.Create();
        factory.Recorder.Answer = (method, _) => method.Name == "CreateInstance" ? PropertyWriterTests.ItemBlock.Create() : null;
        var services = new ServiceCollection()
            .AddSingleton(types.Proxy)
            .AddSingleton(Recorder<IContentRepository>.Create().Proxy)
            .AddSingleton(Recorder<IContentVersionRepository>.Create().Proxy)
            .AddSingleton(Recorder<ILanguageBranchRepository>.Create().Proxy)
            .AddSingleton(Recorder<ITabDefinitionRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("developer"), [])))
            .BuildServiceProvider();
        var call = new CmsCall(services, CancellationToken.None, caller);
        var blocks = new BlockFactory(factory.Proxy, Recorder<EPiServer.Construction.IContentDataBuilder>.Create().Proxy, types.Proxy);
        return new PropertyWriter(call, new ContentLocator(call), blocks, null!, null!, null!);
    }

    private static PropertyWriterTests.LinksBlock Owner(ContentArea? area = null)
    {
        var block = new PropertyWriterTests.LinksBlock();
        block.Property.Add(nameof(PropertyWriterTests.LinksBlock.Area), new PropertyContentArea { PropertyDefinitionID = 2, Value = area });
        return block;
    }

    private static Dictionary<string, JsonElement> Values(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static AgentException Refused(PropertyWriter writer, IContentData owner, string json) =>
        Assert.Throws<AgentException>(() => writer.Apply(owner, Values(json)));

    [Fact]
    public void An_item_is_a_shared_block_or_an_inline_block_not_both_and_not_neither()
    {
        var both = Refused(Writer(), Owner(), """{"Area": [{"ref": "123", "type": "ItemBlock"}]}""");
        Assert.Equal(AgentErrorCodes.Usage, both.Code);
        Assert.Contains("Area[0] gives both a shared block", both.Message);

        var neither = Refused(Writer(), Owner(), """{"Area": [{"displayOption": "wide"}]}""");
        Assert.Equal(AgentErrorCodes.Usage, neither.Code);
        Assert.Contains("Area[0] names no block", neither.Message);
    }

#if CMS13
    private static ContentArea Area(params ContentAreaItem[] items)
    {
        var area = new ContentArea();
        foreach (var item in items)
        {
            area.Items.Add(item);
        }
        return area;
    }

    private static ContentAreaItem Inline(string title, string? secret = null, string? name = null)
    {
        var block = PropertyWriterTests.ItemBlock.Create();
        block.Property["Title"].Value = title;
        block.Property["Secret"].Value = secret;
        var item = new ContentAreaItem();
        InlineBlocks.Set(item, block, ItemType);
        if (name is not null)
        {
            CmsApi.SetRenderSetting(item, InlineBlocks.NameKey, name);
        }
        return item;
    }

    private static List<ContentAreaItem> Items(IContentData owner) => ((ContentArea)owner.Property["Area"].Value).Items.ToList();

    private static object? Value(ContentAreaItem item, string property) => InlineBlocks.Of(item)!.Property[property].Value;

    [Fact]
    public void A_new_inline_block_is_made_as_the_CMS_makes_one_of_its_type_with_the_values_and_name_given()
    {
        var owner = Owner();

        Writer().Apply(owner, Values("""{"Area": [{"type": "itemblock", "name": "Intro", "properties": {"Title": "Hello"}}, {"type": "7"}]}"""));

        var (named, byId) = (Items(owner)[0], Items(owner)[1]);
        Assert.Equal("Hello", Value(named, "Title"));
        Assert.Equal(ItemType, InlineBlocks.TypeId(InlineBlocks.Of(named)!));
        Assert.Equal("Intro", InlineBlocks.Name(named));
        Assert.Equal(ItemType, InlineBlocks.TypeId(InlineBlocks.Of(byId)!));
        Assert.Null(InlineBlocks.Name(byId));
    }

    [Fact]
    public void An_inline_block_written_whole_keeps_the_values_it_leaves_out()
    {
        var owner = Owner(Area(Inline("Old", secret: "kept", name: "Intro")));

        Writer().Apply(owner, Values("""{"Area": [{"type": "ItemBlock", "properties": {"Title": "New"}}]}"""));

        var item = Assert.Single(Items(owner));
        Assert.Equal(("New", "kept"), (Value(item, "Title"), Value(item, "Secret")));
        // The name is as given, like the display option: none here.
        Assert.Null(InlineBlocks.Name(item));
    }

    [Fact]
    public void An_area_written_back_without_one_inline_block_keeps_each_other_block_with_its_own_values()
    {
        var owner = Owner(Area(Inline("A"), Inline("B", secret: "only B's"), Inline("C")));

        // As get shows A and C (B left out): C pairs with itself, not with B, whose values would otherwise stay.
        Writer().Apply(owner, Values("""{"Area": [{"type": "ItemBlock", "properties": {"Title": "A"}}, {"type": "ItemBlock", "properties": {"Title": "C"}}]}"""));

        var items = Items(owner);
        Assert.Equal(["A", "C"], items.Select(i => Value(i, "Title")));
        Assert.All(items, i => Assert.Null(Value(i, "Secret")));
    }

    [Fact]
    public void One_inline_blocks_values_are_set_by_its_position_and_the_rest_of_the_area_stays()
    {
        var owner = Owner(Area(new ContentAreaItem { ContentLink = new ContentReference(123) }, Inline("Old", secret: "kept", name: "Intro")));

        Writer().Apply(owner, Values("""{"Area[1]": {"Title": "New"}}"""));

        var items = Items(owner);
        Assert.Equal(123, items[0].ContentLink.ID);
        Assert.Equal(("New", "kept", "Intro"), (Value(items[1], "Title"), Value(items[1], "Secret"), InlineBlocks.Name(items[1])));
    }

    [Theory]
    [InlineData("""{"Area[0]": {"Title": "x"}}""", "Area[0] is the shared block 123, not an inline block")]
    [InlineData("""{"Area[5]": {"Title": "x"}}""", "'Area' has 2 item(s), so there is no Area[5]")]
    [InlineData("""{"Area[1]": "x"}""", "'Area[1]' takes an object of the inline block's property names")]
    [InlineData("""{"Area[1]": {"Titel": "x"}}""", "Area[1]: 'Titel' is not a property of ItemBlock")]
    [InlineData("""{"Links[0]": {"Title": "x"}}""", "names an item of a ContentArea")]
    [InlineData("""{"Area": [], "Area[1]": {"Title": "x"}}""", "'Area[1]' changes one item of 'Area', which is also given whole")]
    public void A_position_that_isnt_an_inline_block_or_values_that_arent_its_are_refused(string json, string message)
    {
        var owner = Owner(Area(new ContentAreaItem { ContentLink = new ContentReference(123) }, Inline("Old")));
        owner.Property.Add(nameof(PropertyWriterTests.LinksBlock.Links), new PropertyLinkCollection { PropertyDefinitionID = 1 });

        var refused = Refused(Writer(), owner, json);

        Assert.Equal(AgentErrorCodes.Usage, refused.Code);
        Assert.Contains(message, refused.Message);
        Assert.Equal("Old", Value(Items(owner)[1], "Title"));
    }

    [Fact]
    public void An_inline_block_needs_a_block_type()
    {
        var page = Refused(Writer(), Owner(), """{"Area": [{"type": "StandardPage"}]}""");
        Assert.Equal(AgentErrorCodes.Usage, page.Code);
        Assert.Contains("StandardPage isn't a block type", page.Message);

        var missing = Refused(Writer(), Owner(), """{"Area": [{"inline": true, "properties": {"Title": "x"}}]}""");
        Assert.Contains("Area[0] is an inline block without its type", missing.Message);
    }

    [Fact]
    public void Its_snapshot_shows_an_inline_block_as_the_writer_takes_it_so_a_diff_can_be_sent_back()
    {
        var owner = Owner(Area(Inline("Hello", name: "Intro")));

        var item = JsonSerializer.SerializeToElement(PropertyValues.Format(owner.Property["Area"].Value), AgentJson.Options)[0];

        Assert.Equal("""{"inline":true,"type":"ItemBlock","name":"Intro","properties":{"Title":"Hello"}}""", item.GetRawText());
    }
#else
    [Fact]
    public void On_a_CMS_before_12_20_an_inline_block_is_refused_with_the_version_it_needs()
    {
        var refused = Refused(Writer(), Owner(), """{"Area": [{"type": "ItemBlock", "properties": {"Title": "x"}}]}""");

        Assert.Equal(AgentErrorCodes.Usage, refused.Code);
        Assert.Contains("Inline blocks in a ContentArea need CMS 12.20 or later", refused.Message);
    }
#endif
}
