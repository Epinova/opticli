using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Azure;

/// <summary>
/// An Azure Storage connection string, typed at a sealed run's terminal: an account name and key, a blob endpoint with a
/// SAS token, or <c>UseDevelopmentStorage=true</c> (Azurite). The key and token are never printed.
/// </summary>
public sealed class BlobConnection
{
    private BlobConnection(string value, Uri endpoint, string? account, bool sas)
    {
        Value = value;
        Endpoint = endpoint;
        Account = account;
        IsSas = sas;
    }

    /// <summary>The string as typed, for the site and the Blob Storage client. Never printed or written.</summary>
    internal string Value { get; }

    /// <summary>The blob service's address (Azurite's has the account in its path).</summary>
    public Uri Endpoint { get; }

    public string? Account { get; }

    /// <summary>It signs with a SAS token rather than the account key.</summary>
    public bool IsSas { get; }

    /// <summary>Storage on this machine (Azurite): only for a production database that is local (tests).</summary>
    public bool IsDevelopment => Endpoint.IsLoopback;

    /// <exception cref="UsageException">No key or SAS token, not https, or a string the SDK can't read.</exception>
    public static BlobConnection Parse(string value)
    {
        var parts = ConnectionStrings.Parse(value);
        var development = parts.TryGetValue("UseDevelopmentStorage", out var flag) && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        var key = parts.ContainsKey("AccountKey") && parts.ContainsKey("AccountName");
        var sas = parts.TryGetValue("SharedAccessSignature", out var token) && token.Length > 0;
        if (!development && !key && !sas)
        {
            throw NotUsable();
        }
        BlobServiceClient client;
        try
        {
            client = new BlobServiceClient(value.Trim());
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            throw NotUsable();
        }
        if (client.Uri.Scheme != Uri.UriSchemeHttps && !client.Uri.IsLoopback)
        {
            throw new UsageException("Production's blob storage must be reached over https.");
        }
        // The SAS token is part of the client's address: keep only the path.
        var endpoint = new Uri(client.Uri.GetLeftPart(UriPartial.Path));
        return new BlobConnection(value.Trim(), endpoint, client.AccountName, sas && !key);
    }

    private static UsageException NotUsable() => new(
        "That isn't an Azure Storage connection string with a key or a SAS token (AccountName=…;AccountKey=…, or BlobEndpoint=…;SharedAccessSignature=…).",
        "Copy it from the storage account's Access keys in the Azure portal, or from the site's EPiServerAzureBlobs connection string. Microsoft Entra ID (managed identity) connections aren't supported here.");

    public override string ToString() => $"{Endpoint.GetLeftPart(UriPartial.Authority)} ({(IsSas ? "SAS token" : "account key")}, not shown)";
}

/// <summary>A container of production's blob storage, as a sealed run checks it. <see cref="BlobContainer"/> is the real one.</summary>
public interface IMediaContainer
{
    string Name { get; }

    /// <exception cref="UsageException">The key or token was refused.</exception>
    /// <exception cref="UnreachableException">The storage account didn't answer.</exception>
    Task<bool> ExistsAsync(CancellationToken cancellationToken);

    /// <summary>The blob's size; null when it isn't there.</summary>
    /// <param name="blob">The blob's name in the container (<c>&lt;guid&gt;/&lt;file&gt;</c>).</param>
    Task<long?> SizeAsync(string blob, CancellationToken cancellationToken);
}

/// <summary><see cref="IMediaContainer"/> with the Blob Storage SDK.</summary>
public sealed class BlobContainer : IMediaContainer
{
    private readonly BlobConnection _connection;
    private readonly BlobContainerClient _container;

    /// <param name="handler">For tests; null sends over the network.</param>
    public BlobContainer(BlobConnection connection, string container, HttpMessageHandler? handler = null)
    {
        _connection = connection;
        Name = container.Trim();
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(30);
        if (handler is not null)
        {
            options.Transport = new HttpClientTransport(handler);
            options.Retry.MaxRetries = 0;
        }
        _container = new BlobServiceClient(connection.Value, options).GetBlobContainerClient(Name);
    }

    public string Name { get; }

    public Task<bool> ExistsAsync(CancellationToken cancellationToken) =>
        CallAsync("read the container", async () => (await _container.ExistsAsync(cancellationToken)).Value);

    public Task<long?> SizeAsync(string blob, CancellationToken cancellationToken) =>
        CallAsync($"read blob {blob}", async () =>
        {
            try
            {
                return (long?)(await _container.GetBlobClient(blob).GetPropertiesAsync(cancellationToken: cancellationToken)).Value.ContentLength;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        });

    private async Task<T> CallAsync<T>(string what, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw new UsageException($"Production's blob storage refused the connection's {(_connection.IsSas ? "SAS token" : "key")} to {what} ({ex.Status} {ex.ErrorCode}).", "The key or token needs to read, write and list the container.");
        }
        catch (Exception ex) when (ex is RequestFailedException or HttpRequestException or AggregateException or TimeoutException)
        {
            var status = ex is RequestFailedException { Status: > 0 } failed ? $" ({failed.Status}{(failed.ErrorCode is { } code ? $" {code}" : "")})" : "";
            throw new UnreachableException($"Production's blob storage ({_connection.Endpoint.Host}) couldn't {what}{status}: {ServiceBusTopic.FirstLine(ex.Message)}",
                "Check the connection string's account and that this machine reaches it (a firewall or private endpoint may block it).");
        }
    }
}
