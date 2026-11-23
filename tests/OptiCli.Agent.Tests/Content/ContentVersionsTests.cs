using EPiServer.Core;
using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Content;

/// <summary>
/// A version of provider content is that provider's: opticli 0.35 dropped the provider name when it looked up a version of
/// Commerce's catalog content (<c>new ContentReference(id, version)</c>), so a draft of entry 1 loaded the CMS's content 1
/// ("Content 1 has no version 4"), or nothing at all.
/// </summary>
public class ContentVersionsTests
{
    [Theory]
    [InlineData(1, "CatalogContent")]
    [InlineData(-2147483645, "CatalogContent")]
    [InlineData(1073741825, "CatalogContent")]
    [InlineData(63, "dam")]
    public void A_version_of_provider_content_keeps_its_provider(int id, string provider)
    {
        var version = ContentVersions.Of(new ContentReference(id, 0, provider), 4);

        Assert.Equal(id, version.ID);
        Assert.Equal(4, version.WorkID);
        Assert.Equal(provider, version.ProviderName);
    }

    [Fact]
    public void A_version_of_CMS_content_is_the_CMS_content_s()
    {
        var version = ContentVersions.Of(new ContentReference(123), 456);

        Assert.Equal(new ContentReference(123, 456), version);
        Assert.True(string.IsNullOrEmpty(version.ProviderName));
    }
}
