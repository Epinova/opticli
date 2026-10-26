using OptiCli.Core.Azure;

namespace OptiCli.Core.Tests.Azure;

/// <summary>A sealed run offers production's event bus and blob storage only to a build with the CMS's Azure providers, with the settings' names as defaults.</summary>
public sealed class SiteAzureTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(new string[0], false, false)]
    [InlineData(new[] { "EPiServer.Azure.dll" }, true, true)]
    [InlineData(new[] { "EPiServer.Events.Azure.dll" }, true, false)]
    [InlineData(new[] { "EPiServer.Blobs.Azure.dll" }, false, true)]
    public void The_providers_are_in_the_build_or_not(string[] files, bool events, bool blobs)
    {
        foreach (var file in files)
        {
            _folder.Write($"bin/{file}", "");
        }

        Assert.Equal((events, blobs), (SiteAzure.HasEventProvider(_folder.Combine("bin")), SiteAzure.HasBlobProvider(_folder.Combine("bin"))));
    }

    [Fact]
    public void A_setting_the_settings_files_agree_on_is_the_default()
    {
        _folder.Write("appsettings.json", """{"EPiServer": {"Cms": {"AzureBlobProvider": {"ContainerName": "media"}}}}""");
        _folder.Write("appsettings.Production.json", """{"EPiServer": {"Cms": {"AzureBlobProvider": {"ContainerName": "media"}, "AzureEventProvider": {"TopicName": "prod-events"}}}}""");
        _folder.Write("appsettings.Staging.json", """{"EPiServer": {"Cms": {"AzureEventProvider": {"TopicName": "staging-events"}}}}""");

        Assert.Equal("media", SiteAzure.Setting(_folder.Path, SiteAzure.ContainerSetting));
        // They differ per environment: no default, the CMS's then.
        Assert.Null(SiteAzure.Setting(_folder.Path, SiteAzure.TopicSetting));
    }
}
