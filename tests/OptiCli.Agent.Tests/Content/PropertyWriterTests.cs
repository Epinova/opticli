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
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

/// <summary>
/// How a value is written for each caller, where no CMS is needed: links and references an editor may not set are
/// refused, and the developer's are written as given, as before.
/// </summary>
public class PropertyWriterTests
{
    private static (PropertyWriter Writer, CmsCall Call) Writer(CmsCaller caller)
    {
        var types = Recorder<IContentTypeRepository>.Create();
        types.Recorder.Answer = (method, args) => method.Name == "Load" && args is [Type model] ? new ContentType { ID = 1, Name = model.Name, ModelType = model } : null;
        var factory = Recorder<EPiServer.Construction.IContentDataFactory<BlockData>>.Create();
        factory.Recorder.Answer = (method, _) => method.Name == "CreateInstance" ? ItemBlock.Create() : null;
        var services = new ServiceCollection()
            .AddSingleton(Recorder<IContentRepository>.Create().Proxy)
            .AddSingleton(Recorder<IContentVersionRepository>.Create().Proxy)
            .AddSingleton(Recorder<ILanguageBranchRepository>.Create().Proxy)
            .AddSingleton(Recorder<ITabDefinitionRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("editor@example.com"), [])))
            .BuildServiceProvider();
        var call = new CmsCall(services, CancellationToken.None, caller);
        var blocks = new BlockFactory(factory.Proxy, Recorder<EPiServer.Construction.IContentDataBuilder>.Create().Proxy, types.Proxy);
        return (new PropertyWriter(call, new ContentLocator(call), blocks, null!, null!, null!), call);
    }

    private static readonly ContentType[] PageTypeChoices =
    [
        new PageType { ID = 19, GUID = Guid.Parse("9ccc8a41-5c8c-4be0-8e73-520ff3de8267"), Name = "StandardPage" },
        new PageType { ID = 20, Name = "ArticlePage" },
        new ContentType { ID = 30, Name = "TeaserBlock" },
        new PageType { ID = 40, Name = "NewsPage" },
        new PageType { ID = 41, Name = "Newspage" },
    ];

    /// <summary>A developer's writer whose site has <see cref="PageTypeChoices"/>, and a block with a PageType property.</summary>
    private static (PropertyWriter Writer, LinksBlock Block) PageTypeWriter()
    {
        var types = Recorder<IContentTypeRepository>.Create();
        types.Recorder.Answer = (method, args) => (method.Name, args) switch
        {
            ("Load", [int id]) => PageTypeChoices.FirstOrDefault(t => t.ID == id),
            ("Load", [Guid guid]) => PageTypeChoices.FirstOrDefault(t => t.GUID == guid),
            // As CMS 12 does: by exact name (CMS 13 ignores case; the writer must take either).
            ("Load", [string name]) => PageTypeChoices.FirstOrDefault(t => t.Name == name),
            ("List", []) => PageTypeChoices,
            _ => null,
        };
        var services = new ServiceCollection()
            .AddSingleton(types.Proxy)
            .AddSingleton(Recorder<IContentRepository>.Create().Proxy)
            .AddSingleton(Recorder<IContentVersionRepository>.Create().Proxy)
            .AddSingleton(Recorder<ILanguageBranchRepository>.Create().Proxy)
            .AddSingleton(Recorder<ITabDefinitionRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("developer"), [])))
            .BuildServiceProvider();
        var call = new CmsCall(services, CancellationToken.None, CmsCaller.Developer);
        var block = Block();
        block.Property.Add("Filter", new PropertyPageType { PropertyDefinitionID = 4 });
        return (new PropertyWriter(call, new ContentLocator(call), null!, null!, null!, null!), block);
    }

    [Theory]
    [InlineData("\"StandardPage\"")]
    [InlineData("\"standardpage\"")]
    [InlineData("19")]
    [InlineData("\"19\"")]
    [InlineData("\"9ccc8a41-5c8c-4be0-8e73-520ff3de8267\"")]
    public void A_PageType_property_takes_the_page_type_by_name_as_get_shows_it_or_by_id_or_GUID(string json)
    {
        var (writer, block) = PageTypeWriter();

        writer.Apply(block, new Dictionary<string, JsonElement> { ["Filter"] = JsonDocument.Parse(json).RootElement });

        Assert.Equal(19, block.Property["Filter"].Value);
    }

    [Fact]
    public void A_PageType_property_is_cleared_by_an_empty_string_as_before()
    {
        var (writer, block) = PageTypeWriter();
        writer.Apply(block, new Dictionary<string, JsonElement> { ["Filter"] = JsonDocument.Parse("\"StandardPage\"").RootElement });

        writer.Apply(block, new Dictionary<string, JsonElement> { ["Filter"] = JsonDocument.Parse("\"\"").RootElement });

        Assert.Null(block.Property["Filter"].Value);
    }

    [Fact]
    public void A_PageType_name_in_another_case_is_found_unless_several_types_match_it()
    {
        var (writer, block) = PageTypeWriter();

        writer.Apply(block, new Dictionary<string, JsonElement> { ["Filter"] = JsonDocument.Parse("\"articlepage\"").RootElement });
        Assert.Equal(20, block.Property["Filter"].Value);

        // The exact name wins; two case-insensitive matches are ambiguous.
        writer.Apply(block, new Dictionary<string, JsonElement> { ["Filter"] = JsonDocument.Parse("\"Newspage\"").RootElement });
        Assert.Equal(41, block.Property["Filter"].Value);
        var ambiguous = Assert.Throws<AgentException>(() => writer.Apply(block, new Dictionary<string, JsonElement> { ["Filter"] = JsonDocument.Parse("\"newspage\"").RootElement }));
        Assert.Equal(AgentErrorCodes.Usage, ambiguous.Code);
        Assert.Contains("NewsPage, Newspage", ambiguous.Message);
    }

    [Fact]
    public void A_PageType_property_refuses_another_kind_of_type_as_a_validation_error_and_names_an_unknown_one()
    {
        var (writer, block) = PageTypeWriter();

        var notAPage = Assert.Throws<AgentException>(() => writer.Apply(block, new Dictionary<string, JsonElement> { ["Filter"] = JsonDocument.Parse("\"TeaserBlock\"").RootElement }));
        Assert.Equal(AgentErrorCodes.Validation, notAPage.Code);
        Assert.Contains("TeaserBlock isn't a page type", Assert.Single(notAPage.Validation!).Message);
        Assert.Equal(AgentErrorCodes.Validation, Assert.Throws<AgentException>(() => writer.Apply(block, new Dictionary<string, JsonElement> { ["Filter"] = JsonDocument.Parse("30").RootElement })).Code);

        var unknown = Assert.Throws<AgentException>(() => writer.Apply(block, new Dictionary<string, JsonElement> { ["Filter"] = JsonDocument.Parse("\"StandardPag\"").RootElement }));
        Assert.Equal(AgentErrorCodes.Usage, unknown.Code);
        Assert.Contains("StandardPage", unknown.Hint);
        Assert.DoesNotContain("TeaserBlock", unknown.Hint);
    }

    private static LinksBlock Block()
    {
        var block = new LinksBlock();
        block.Property.Add(nameof(LinksBlock.Links), new PropertyLinkCollection { PropertyDefinitionID = 1 });
        block.Property.Add(nameof(LinksBlock.Area), new PropertyContentArea { PropertyDefinitionID = 2 });
        return block;
    }

    private static Dictionary<string, JsonElement> Values(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("jav&#x61;script:alert(1)")]
    [InlineData("data:text/html,x")]
    public void A_link_that_could_run_script_is_refused_for_an_editor_and_written_as_given_for_the_developer(string href)
    {
        var values = Values(new { Links = new[] { new { href, text = "Click" } } });

        var refused = Assert.Throws<AgentException>(() => Writer(CmsCaller.Editor).Writer.Apply(Block(), values));
        Assert.Equal(AgentErrorCodes.Usage, refused.Code);
        Assert.Contains("'Links' has a", refused.Message);

        var block = Block();
        Writer(CmsCaller.Developer).Writer.Apply(block, values);
        Assert.Equal(href, Assert.Single((LinkItemCollection)block.Property["Links"].Value).Href);
    }

    [Fact]
    public void Script_in_rich_text_is_refused_for_an_editor_and_written_as_given_for_the_developer()
    {
        const string body = "<p>Hi</p><img src=x onerror=alert(1)>";
        var values = Values(new { Body = body });
        LinksBlock WithBody()
        {
            var block = Block();
            block.Property.Add(nameof(LinksBlock.Body), new PropertyXhtmlString { PropertyDefinitionID = 3 });
            return block;
        }

        var refused = Assert.Throws<AgentException>(() => Writer(CmsCaller.Editor).Writer.Apply(WithBody(), values));
        Assert.Contains("'Body' has an event handler attribute 'onerror' on <img>", refused.Message);

        var written = WithBody();
        Writer(CmsCaller.Developer).Writer.Apply(written, values);
        Assert.Equal(body, ((XhtmlString)written.Property["Body"].Value).ToInternalString());
    }

    [Fact]
    public void A_web_link_is_written_for_an_editor()
    {
        var block = Block();
        Writer(CmsCaller.Editor).Writer.Apply(block, Values(new { Links = new[] { new { href = "https://example.com/", text = "Example" } } }));
        Assert.Equal("https://example.com/", Assert.Single((LinkItemCollection)block.Property["Links"].Value).Href);
    }

    [Theory]
    [InlineData("\"123\"")]
    [InlineData("123")]
    public void A_ContentArea_given_as_text_is_refused_for_an_editor_whatever_it_names(string json)
    {
        var values = new Dictionary<string, JsonElement> { ["Area"] = JsonDocument.Parse(json).RootElement };

        var refused = Assert.Throws<AgentException>(() => Writer(CmsCaller.Editor).Writer.Apply(Block(), values));

        Assert.Equal(AgentErrorCodes.Usage, refused.Code);
        Assert.Contains("takes an array", refused.Message);
        Assert.Contains("[{\"ref\": \"123\"}]", refused.Hint);
    }

    [Fact]
    public void Links_given_as_stored_markup_are_refused_for_an_editor_as_every_attribute_in_it_is_rendered()
    {
        var values = Values(new { Links = "<links><a href=\"/en/\" onclick=\"alert(document.domain)\">x</a></links>" });

        var refused = Assert.Throws<AgentException>(() => Writer(CmsCaller.Editor).Writer.Apply(Block(), values));
        Assert.Contains("takes links as objects", refused.Message);
    }

    [Fact]
    public void What_the_value_already_had_may_be_written_back_but_nothing_new_and_no_more_of_it()
    {
        const string video = "<iframe src=\"https://www.youtube.com/embed/abc\" width=\"560\"></iframe>";
        LinksBlock Stored()
        {
            var block = Block();
            block.Property.Add(nameof(LinksBlock.Body), new PropertyXhtmlString { PropertyDefinitionID = 3, Value = new XhtmlString($"<p>Watch teh video</p>{video}") });
            return block;
        }
        var editor = Writer(CmsCaller.Editor).Writer;

        // The typo fixed, the video as it was. (Links written back are checked on the test site: replacing a link
        // collection makes the CMS compare permanent links, which needs a CMS.)
        editor.Apply(Stored(), Values(new { Body = $"<p>Watch the video</p>{video}" }));
        // Moved within the same value is the same construct.
        editor.Apply(Stored(), Values(new { Body = $"{video}<p>Watch the video</p>" }));

        Assert.Contains("<iframe>", Assert.Throws<AgentException>(() => editor.Apply(Stored(), Values(new { Body = $"<p>Twice</p>{video}{video}" }))).Message);
        Assert.Throws<AgentException>(() => editor.Apply(Stored(), Values(new { Body = video.Replace("abc", "other") })));
        Assert.Throws<AgentException>(() => editor.Apply(Stored(), Values(new { Body = $"{video}<img src=x onerror=alert(1)>" })));
        // Not into a property that didn't have it.
        Assert.Throws<AgentException>(() => editor.Apply(Block(), Values(new { Links = new[] { new { href = "sms:+4712345678", text = "Text us" } } })));
    }

    [Theory]
    [InlineData("<textarea>{0}</textarea>")]
    [InlineData("<title>{0}</title>")]
    [InlineData("<noscript>{0}</noscript>")]
    [InlineData("<xmp>{0}</xmp>")]
    [InlineData("<noembed>{0}</noembed>")]
    [InlineData("<noframes>{0}</noframes>")]
    [InlineData("<style>{0}</style>")]
    [InlineData("<script>{0}</script>")]
    [InlineData("<iframe>{0}</iframe>")]
    [InlineData("<plaintext>{0}")]
    [InlineData("<template>{0}</template>")]
    [InlineData("<select>{0}</select>")]
    [InlineData("<svg>{0}</svg>")]
    [InlineData("<math>{0}</math>")]
    [InlineData("<object data=\"x\">{0}</object>")]
    [InlineData("<![CDATA[x]]>{0}")]
    public void What_was_inert_in_the_stored_value_isnt_written_back_live_nor_the_other_way(string container)
    {
        const string script = "<img src=x onerror=alert(1)>";
        var inert = string.Format(container, script);
        var empty = string.Format(container, "");
        LinksBlock Stored(string body)
        {
            var block = Block();
            block.Property.Add(nameof(LinksBlock.Body), new PropertyXhtmlString { PropertyDefinitionID = 3, Value = new XhtmlString(body) });
            return block;
        }
        var editor = Writer(CmsCaller.Editor).Writer;

        // Inert in a container there, live here: refused.
        Assert.Contains("'onerror'", Assert.Throws<AgentException>(() => editor.Apply(Stored(inert), Values(new { Body = $"<p>hi</p>{script}" }))).Message);
        // Live there, moved into the container here: refused too (the container itself is what it was).
        Assert.Throws<AgentException>(() => editor.Apply(Stored(empty + script), Values(new { Body = inert })));
        // The same live construct, in the same place: still the editor's to write back.
        editor.Apply(Stored($"<p>a</p>{script}"), Values(new { Body = $"<p>b</p>{script}" }));
        // And the developer writes what they write.
        Writer(CmsCaller.Developer).Writer.Apply(Stored(inert), Values(new { Body = script }));
    }

    [Theory]
    [InlineData("javascrip\u0001t:alert(1)")]
    [InlineData("/en/\u0000x")]
    [InlineData("https://example.com/\u001F")]
    public void A_link_with_a_control_character_is_refused_for_an_editor_before_the_save(string href)
    {
        var refused = Assert.Throws<AgentException>(() => Writer(CmsCaller.Editor).Writer.Apply(Block(), Values(new { Links = new[] { new { href, text = "x" } } })));
        Assert.Contains("control character", refused.Message);
    }

    [Fact]
    public void A_block_list_keeps_what_an_editor_cant_see_of_its_items_and_cant_drop_it()
    {
        LinksBlock Stored()
        {
            var block = Block();
            var item = ItemBlock.Create();
            item.Property["Title"].Value = "First";
            item.Property["Secret"].Value = "kept";
            block.Property.Add(nameof(LinksBlock.Items), new PropertyItems { PropertyDefinitionID = 4, Value = new List<ItemBlock> { item } });
            return block;
        }

        var written = Stored();
        Writer(CmsCaller.Editor).Writer.Apply(written, Values(new { Items = new[] { new { Title = "Renamed" } } }));
        var item = Assert.Single((List<ItemBlock>)written.Property["Items"].Value);
        Assert.Equal(("Renamed", "kept"), ((string)item.Property["Title"].Value, (string)item.Property["Secret"].Value));

        var dropped = Assert.Throws<AgentException>(() => Writer(CmsCaller.Editor).Writer.Apply(Stored(), Values(new { Items = Array.Empty<object>() })));
        Assert.Contains("would lose items", dropped.Message);
        Assert.Throws<AgentException>(() => Writer(CmsCaller.Editor).Writer.Apply(Stored(), Values(new { Items = new[] { new { Secret = "x" } } })));

        // The developer writes the list as given.
        var developer = Stored();
        Writer(CmsCaller.Developer).Writer.Apply(developer, Values(new { Items = new[] { new { Title = "Renamed" } } }));
        Assert.Null(Assert.Single((List<ItemBlock>)developer.Property["Items"].Value).Property["Secret"].Value);
    }

    [Fact]
    public void A_link_in_a_list_propertys_items_is_checked_too()
    {
        LinksBlock WithTeasers()
        {
            var block = Block();
            block.Property.Add(nameof(LinksBlock.Teasers), new PropertyTeasers { PropertyDefinitionID = 5 });
            return block;
        }
        var values = Values(new { Teasers = new[] { new { Text = "Read", TargetUrl = "javascript:alert(1)" } } });

        Assert.Contains("javascript:", Assert.Throws<AgentException>(() => Writer(CmsCaller.Editor).Writer.Apply(WithTeasers(), values)).Message);
        Writer(CmsCaller.Developer).Writer.Apply(WithTeasers(), values);
        Writer(CmsCaller.Editor).Writer.Apply(WithTeasers(), Values(new { Teasers = new[] { new { Text = "Read", TargetUrl = "/en/" } } }));
    }

    [Fact]
    public void A_value_in_get_contents_shape_or_one_JSON_cant_make_is_a_usage_error()
    {
        var block = Block();
        block.Property.Add("Address", new PropertyUrl { PropertyDefinitionID = 6 });

        var shape = Assert.Throws<AgentException>(() => Writer(CmsCaller.Developer).Writer.Apply(block, Values(new { Address = new { type = "Url", value = "/en/" } })));
        Assert.Equal(AgentErrorCodes.Usage, shape.Code);
        Assert.Contains("{type, value}", shape.Message);
        var url = Assert.Throws<AgentException>(() => Writer(CmsCaller.Developer).Writer.Apply(block, Values(new { Address = new { href = "/en/" } })));
        Assert.Equal(AgentErrorCodes.Usage, url.Code);
    }

    public class LinksBlock : BlockData
    {
        public virtual LinkItemCollection? Links { get; set; }

        public virtual ContentArea? Area { get; set; }

        public virtual XhtmlString? Body { get; set; }

        public virtual IList<ItemBlock>? Items { get; set; }

        public virtual IList<Teaser>? Teasers { get; set; }
    }

    /// <summary>A block list's item type, with a property the edit UI doesn't show.</summary>
    public class ItemBlock : BlockData
    {
        public virtual string? Title { get; set; }

        [System.ComponentModel.DataAnnotations.ScaffoldColumn(false)]
        public virtual string? Secret { get; set; }

        public static ItemBlock Create()
        {
            var block = new ItemBlock();
            block.Property.Add(nameof(Title), new PropertyString { PropertyDefinitionID = 1 });
            block.Property.Add(nameof(Secret), new PropertyString { PropertyDefinitionID = 2, DisplayEditUI = false });
            return block;
        }
    }

    /// <summary>A list property's item (as of <c>PropertyList&lt;T&gt;</c>) with a URL as text.</summary>
    public class Teaser
    {
        public string? Text { get; set; }

        public string? TargetUrl { get; set; }
    }

    /// <summary>A block list property, as CMS 12.20 and later have them.</summary>
    private sealed class PropertyItems : PropertyData
    {
        private object? _value;

        public override object? Value { get => _value; set => _value = value; }

        public override PropertyDataType Type => PropertyDataType.LongString;

        public override Type PropertyValueType => typeof(IList<ItemBlock>);

        public override void ParseToSelf(string value) => throw new NotSupportedException();

        protected override void SetDefaultValue() => _value = null;
    }

    /// <summary>A list property of plain objects (<c>PropertyList&lt;Teaser&gt;</c>).</summary>
    private sealed class PropertyTeasers : PropertyData
    {
        private object? _value;

        public override object? Value { get => _value; set => _value = value; }

        public override PropertyDataType Type => PropertyDataType.LongString;

        public override Type PropertyValueType => typeof(IList<Teaser>);

        public override void ParseToSelf(string value) => throw new NotSupportedException();

        protected override void SetDefaultValue() => _value = null;
    }
}
