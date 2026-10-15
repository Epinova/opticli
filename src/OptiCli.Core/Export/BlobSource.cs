using System.Net;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Protocol;

namespace OptiCli.Core.Export;

/// <summary>
/// Where an export finds media files: a folder holding the blob container (a copy of a site's blob folder, or the local
/// site's own), or an Azure blob container's HTTPS URL with a SAS token. A media version's blob URI
/// (<c>epi.fx.blob://default/&lt;container&gt;/&lt;file&gt;</c>) names the file as <c>&lt;container&gt;/&lt;file&gt;</c> in both.
/// </summary>
public sealed class BlobSource : IDisposable
{
    private readonly string? _folder;
    private readonly Uri? _container;
    private readonly HttpClient? _http;

    private BlobSource(string? folder, Uri? container)
    {
        _folder = folder;
        _container = container;
        _http = container is null ? null : new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>What <c>--blobs</c> names, for messages: the folder, or the container URL without its token.</summary>
    public string Description => _folder ?? Redacted(_container!);

    /// <summary><c>--blobs</c>: an existing folder, or an <c>https://</c> container URL (its query is the SAS token).</summary>
    /// <exception cref="UsageException">Neither.</exception>
    public static BlobSource Parse(string value, string currentDirectory)
    {
        var text = value.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("--blobs takes an https:// container URL (a SAS token travels in it), not http://.");
        }
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(text, UriKind.Absolute, out var url)
                ? new BlobSource(null, url)
                : throw new UsageException("--blobs is not a valid URL.", "Give the blob container's URL with a SAS token that may read and list: https://<account>.blob.core.windows.net/<container>?<token>.");
        }
        var folder = Path.GetFullPath(text, currentDirectory);
        return Directory.Exists(folder)
            ? new BlobSource(folder, null)
            : throw new UsageException($"--blobs: the folder {folder} doesn't exist.", "Give the folder that holds the blob containers (a copy of the site's App_Data/blobs), or the container's https:// URL with a SAS token.");
    }

    /// <summary>The local site's own blob folder (its file blob provider's).</summary>
    public static BlobSource Local(string folder) => new(folder, null);

    /// <summary>Copies the file a blob URI names to <paramref name="destination"/>.</summary>
    /// <returns>Null when copied, else why not (missing, too large, refused).</returns>
    public async Task<string?> CopyAsync(string blobUri, string destination, CancellationToken cancellationToken)
    {
        var (_, container, file) = BlobLocator.ParseUri(blobUri);
        if (container is null || file is null)
        {
            return $"its blob URI {blobUri} names no container and file";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (_folder is not null)
        {
            // The URI comes from the source database: it names a file in the blob folder, never one elsewhere.
            var source = Path.GetFullPath(Path.Combine(_folder, container, file));
            if (!source.StartsWith(Path.TrimEndingDirectorySeparator(_folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return $"its blob URI {blobUri} names a file outside the blob folder";
            }
            var info = new FileInfo(source);
            if (!info.Exists)
            {
                return $"{source} doesn't exist";
            }
            if (info.Length > UploadRequest.MaxBytes)
            {
                return TooLarge(info.Length);
            }
            File.Copy(source, destination, overwrite: true);
            return null;
        }

        var url = new UriBuilder(_container!);
        url.Path = $"{url.Path.TrimEnd('/')}/{Uri.EscapeDataString(container)}/{Uri.EscapeDataString(file)}";
        // The client's timeout ends with the headers; the whole download gets as long.
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        transfer.CancelAfter(_http!.Timeout);
        try
        {
            using var response = await _http.GetAsync(url.Uri, HttpCompletionOption.ResponseHeadersRead, transfer.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Never the URL itself: its query is the token.
                return response.StatusCode switch
                {
                    HttpStatusCode.NotFound => $"{container}/{file} isn't in the container",
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => $"the container refused {container}/{file} ({(int)response.StatusCode}): check the SAS token's read permission and expiry",
                    _ => $"the container answered {(int)response.StatusCode} for {container}/{file}",
                };
            }
            if (response.Content.Headers.ContentLength > UploadRequest.MaxBytes)
            {
                return TooLarge(response.Content.Headers.ContentLength.Value);
            }
            long written;
            await using (var body = await response.Content.ReadAsStreamAsync(transfer.Token))
            await using (var target = File.Create(destination))
            {
                // A response without a length is counted as it comes.
                written = await CopyAtMostAsync(body, target, UploadRequest.MaxBytes, transfer.Token);
            }
            if (written > UploadRequest.MaxBytes)
            {
                File.Delete(destination);
                return TooLarge(written);
            }
            return null;
        }
        catch (HttpRequestException ex)
        {
            File.Delete(destination);
            return $"{container}/{file} couldn't be fetched ({ex.HttpRequestError})";
        }
        catch (IOException)
        {
            // The connection broke while reading.
            File.Delete(destination);
            return $"{container}/{file} couldn't be read to the end";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            File.Delete(destination);
            return $"fetching {container}/{file} timed out";
        }
    }

    /// <summary>Copies up to one byte more than <paramref name="limit"/>; returns how many were copied.</summary>
    private static async Task<long> CopyAtMostAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while (total <= limit && (read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
        return total;
    }

    private static string TooLarge(long bytes) =>
        $"its file is {bytes / (1024 * 1024)} MB, and a plan uploads at most {UploadRequest.MaxBytes / (1024 * 1024)} MB";

    private static string Redacted(Uri url) => url.GetLeftPart(UriPartial.Path);

    public void Dispose() => _http?.Dispose();
}
