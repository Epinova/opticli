using System.Net;
using OptiCli.Core.Azure;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Tests.Azure;

/// <summary>
/// Production's blob storage through the Blob Storage SDK: the connection string typed at a sealed run, and the container
/// and blob checks with the SDK's HTTP transport answered here.
/// </summary>
public sealed class BlobStorageTests
{
    private const string AccountKey = "DefaultEndpointsProtocol=https;AccountName=prodmedia;AccountKey=YmxvYi1zZWNyZXQ=;EndpointSuffix=core.windows.net";
    private const string AzuriteKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    [Fact]
    public void An_account_key_a_sas_token_and_azurite_are_taken_and_never_printed()
    {
        var key = BlobConnection.Parse(AccountKey);
        var sas = BlobConnection.Parse("BlobEndpoint=https://prodmedia.blob.core.windows.net;SharedAccessSignature=sv=2022-11-02&sig=c2FzLXNlY3JldA%3D");
        var azurite = BlobConnection.Parse("UseDevelopmentStorage=true");
        var azuriteByAddress = BlobConnection.Parse($"AccountName=devstoreaccount1;AccountKey={AzuriteKey};BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1");

        Assert.Equal(("https://prodmedia.blob.core.windows.net/", "prodmedia", false, false), (key.Endpoint.AbsoluteUri, key.Account, key.IsSas, key.IsDevelopment));
        Assert.Equal(("https://prodmedia.blob.core.windows.net/", true), (sas.Endpoint.AbsoluteUri, sas.IsSas));
        Assert.True(azurite.IsDevelopment);
        Assert.True(azuriteByAddress.IsDevelopment);
        Assert.DoesNotContain("YmxvYi1zZWNyZXQ", key.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("c2FzLXNlY3JldA", sas.ToString() + sas.Endpoint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AccountName=prodmedia", "a key or a SAS token")]
    [InlineData("BlobEndpoint=http://media.example.net;SharedAccessSignature=sv=1&sig=x", "https")]
    [InlineData("https://prodmedia.blob.core.windows.net/media", "a key or a SAS token")]
    public void Other_forms_are_refused(string value, string expected)
    {
        Assert.Contains(expected, Assert.Throws<UsageException>(() => BlobConnection.Parse(value)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_container_and_a_blobs_size_are_read_with_the_key()
    {
        var http = new FakeHttp()
            .On("GET", "/media", HttpStatusCode.OK)
            .On("HEAD", "/media/0d6f3a528b1e4c7a9e255a4c1b7d3f90/logo%20one.png", HttpStatusCode.OK, "", r => r.Content.Headers.ContentLength = 1234)
            .On("HEAD", "/media/0d6f3a528b1e4c7a9e255a4c1b7d3f90/missing.png", HttpStatusCode.NotFound, "", r => r.Headers.Add("x-ms-error-code", "BlobNotFound"));
        var container = new BlobContainer(BlobConnection.Parse(AccountKey), "media", http);

        Assert.True(await container.ExistsAsync(CancellationToken.None));
        Assert.Equal(1234, await container.SizeAsync("0d6f3a528b1e4c7a9e255a4c1b7d3f90/logo one.png", CancellationToken.None));
        Assert.Null(await container.SizeAsync("0d6f3a528b1e4c7a9e255a4c1b7d3f90/missing.png", CancellationToken.None));
        Assert.Contains("restype=container", http.Requests[0].Request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.All(http.Requests, r => Assert.StartsWith("SharedKey prodmedia:", r.Request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sas_token_goes_in_the_query_and_a_missing_container_is_false()
    {
        var http = new FakeHttp().On("GET", "/media", HttpStatusCode.NotFound,
            """<?xml version="1.0" encoding="utf-8"?><Error><Code>ContainerNotFound</Code><Message>The specified container does not exist.</Message></Error>""",
            r =>
            {
                // As Azure answers: the error's code in a header and in an XML body.
                r.Headers.Add("x-ms-error-code", "ContainerNotFound");
                r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
            });
        var container = new BlobContainer(BlobConnection.Parse("BlobEndpoint=https://prodmedia.blob.core.windows.net/;SharedAccessSignature=sv=2022-11-02&sig=abc"), "media", http);

        Assert.False(await container.ExistsAsync(CancellationToken.None));
        Assert.Contains("sig=abc", http.Requests[0].Request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.False(http.Requests[0].Request.Headers.Contains("Authorization"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, typeof(UsageException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(UnreachableException))]
    public async Task A_refused_key_is_the_users_to_fix_and_anything_else_didnt_answer(HttpStatusCode status, Type expected)
    {
        var http = new FakeHttp().On("GET", "/media", status);
        var container = new BlobContainer(BlobConnection.Parse(AccountKey), "media", http);

        var failed = await Assert.ThrowsAnyAsync<OptiCliException>(() => container.ExistsAsync(CancellationToken.None));

        Assert.IsType(expected, failed);
        Assert.DoesNotContain("YmxvYi1zZWNyZXQ", failed.Message + failed.Hint, StringComparison.Ordinal);
    }
}
