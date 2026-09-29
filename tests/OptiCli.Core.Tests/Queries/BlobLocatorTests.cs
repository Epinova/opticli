using OptiCli.Core.Queries;

namespace OptiCli.Core.Tests.Queries;

public class BlobLocatorTests
{
    private const string Uri = "epi.fx.blob://default/0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210.jpg";

    [Fact]
    public void Parses_provider_container_and_file()
    {
        Assert.Equal(("default", "0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210.jpg"), BlobLocator.ParseUri(Uri));
        Assert.Equal((null, null, null), BlobLocator.ParseUri("https://cdn.example.com/a.jpg"));
    }

    [Fact]
    public void Default_root_is_app_data_blobs_under_the_project()
    {
        var (root, source) = BlobLocator.ResolveRoot(new Dictionary<string, (string?, string)>(), "/work/site");

        Assert.Equal(Path.GetFullPath("/work/site/App_Data/blobs"), root);
        Assert.Equal("CMS default", source);
    }

    [Fact]
    public void Configured_path_and_app_data_path_are_honoured()
    {
        var settings = new Dictionary<string, (string?, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["EPiServer:Cms:FileBlobProvider:Path"] = (@"[appDataPath]\media", "appsettings.Development.json"),
            ["EPiServer:Cms:Environment:AppDataPath"] = ("../data", "appsettings.json"),
        };

        var (root, source) = BlobLocator.ResolveRoot(settings, "/work/site");

        Assert.Equal(Path.GetFullPath("/work/data/media"), root);
        Assert.Equal("EPiServer:Cms:FileBlobProvider:Path in appsettings.Development.json", source);
    }

    [Fact]
    public void Locates_the_file_and_reports_whether_it_exists()
    {
        using var temp = new TempDirectory();
        temp.Write("site/appsettings.Development.json", """{ "EPiServer": { "Cms": { "FileBlobProvider": { "Path": "[appDataPath]\\blobs" } } } }""");
        temp.Write("site/App_Data/blobs/0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210.jpg", "12345");

        var found = BlobLocator.Locate(Uri, temp.Combine("site"));
        Assert.True(found.Exists);
        Assert.Equal(5, found.Size);
        Assert.Equal(temp.Combine("site/App_Data/blobs/0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210.jpg"), found.Path);

        var missing = BlobLocator.Locate(Uri.Replace(".jpg", ".png"), temp.Combine("site"));
        Assert.False(missing.Exists);
        Assert.Null(missing.Size);
    }
}
