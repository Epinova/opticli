using OptiCli.Core.Errors;
using OptiCli.Core.Export;

namespace OptiCli.Core.Tests.Export;

public class BlobSourceTests
{
    private const string Uri = "epi.fx.blob://default/155cba62ae10561db0c6831fbd91606f/695de89db07d4067a44c5b31c99f5f6d.png";

    [Fact]
    public async Task A_folder_holds_the_containers_and_a_missing_file_says_where_it_looked()
    {
        using var temp = new TempDirectory();
        temp.Write("blobs/155cba62ae10561db0c6831fbd91606f/695de89db07d4067a44c5b31c99f5f6d.png", "png");
        using var source = BlobSource.Parse("blobs", temp.Path);

        Assert.Null(await source.CopyAsync(Uri, temp.Combine("out/files/a.png"), CancellationToken.None));
        Assert.Equal("png", File.ReadAllText(temp.Combine("out/files/a.png")));

        var missing = await source.CopyAsync(Uri.Replace(".png", ".jpg"), temp.Combine("out/files/b.jpg"), CancellationToken.None);
        Assert.Contains("695de89db07d4067a44c5b31c99f5f6d.jpg doesn't exist", missing);
        Assert.Contains("names no container", await source.CopyAsync("https://cdn.example.com/a.png", temp.Combine("out/c.png"), CancellationToken.None));

        // The URI comes from the source database: an escaped path out of the blob folder isn't followed.
        temp.Write("secret.txt", "secret");
        var outside = await source.CopyAsync("epi.fx.blob://default/155cba62ae10561db0c6831fbd91606f/..%2F..%2Fsecret.txt", temp.Combine("out/d.txt"), CancellationToken.None);
        Assert.Contains("outside the blob folder", outside);
        Assert.False(File.Exists(temp.Combine("out/d.txt")));
    }

    [Fact]
    public void A_container_url_is_https_and_never_shown_with_its_token()
    {
        using var source = BlobSource.Parse("https://account.blob.core.windows.net/mysitemedia?sv=2024&sig=secret", "/");
        Assert.Equal("https://account.blob.core.windows.net/mysitemedia", source.Description);
        Assert.DoesNotContain("secret", source.Description);

        Assert.Contains("https://", Assert.Throws<UsageException>(() => BlobSource.Parse("http://account.blob.core.windows.net/media?sig=x", "/")).Message);
        Assert.Contains("doesn't exist", Assert.Throws<UsageException>(() => BlobSource.Parse("no-such-folder", Path.GetTempPath())).Message);
    }
}
