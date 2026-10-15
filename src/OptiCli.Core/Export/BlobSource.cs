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
            var source = Path.Combine(_folder, container, file);
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
        try
        {
            using var response = await _http!.GetAsync(url.Uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            await using (var target = File.Create(destination))
            {
                await response.Content.CopyToAsync(target, cancellationToken);
            }
            return null;
        }
        catch (HttpRequestException ex)
        {
            File.Delete(destination);
            return $"{container}/{file} couldn't be fetched ({ex.HttpRequestError})";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            File.Delete(destination);
            return $"fetching {container}/{file} timed out";
        }
    }

    private static string TooLarge(long bytes) =>
        $"its file is {bytes / (1024 * 1024)} MB, and a plan uploads at most {UploadRequest.MaxBytes / (1024 * 1024)} MB";

    private static string Redacted(Uri url) => url.GetLeftPart(UriPartial.Path);

    public void Dispose() => _http?.Dispose();
}
