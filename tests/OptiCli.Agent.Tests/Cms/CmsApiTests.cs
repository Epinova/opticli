using EPiServer.Core;
using EPiServer.Core.Html.StringParsing;
using OptiCli.Cms.Compat;
using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>The adapters that let the operations compile against CMS 12 and CMS 13 alike; run on both builds.</summary>
public class CmsApiTests
{
    [Fact]
    public void A_new_item_has_no_render_settings_until_one_is_set()
    {
        var item = new ContentAreaItem { ContentLink = new ContentReference(7) };

        Assert.Empty(CmsApi.RenderSettings(item));
        Assert.Empty(CmsApi.RenderSettings(null));

        CmsApi.SetRenderSetting(item, PropertyValues.DisplayOptionKey, "wide");
        CmsApi.SetRenderSetting(item, "data-extra", "1");

        Assert.Equal("wide", PropertyValues.DisplayOption(item));
        Assert.Equal(
            [(ContentFragment.ContentDisplayOptionAttributeName, "wide"), ("data-extra", "1")],
            CmsApi.RenderSettings(item).Select(s => (s.Key, s.Value.ToString())).OrderBy(s => s.Key == "data-extra"));
    }

    [Fact]
    public void Render_settings_survive_a_writable_clone()
    {
        var item = new ContentAreaItem { ContentLink = new ContentReference(7) };
        CmsApi.SetRenderSetting(item, PropertyValues.DisplayOptionKey, "narrow");

        Assert.Equal("narrow", PropertyValues.DisplayOption(item.CreateWritableClone()));
    }
}
