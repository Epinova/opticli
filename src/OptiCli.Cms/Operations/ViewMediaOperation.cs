using System.Text;
using System.Text.Json.Serialization;
using EPiServer.Core;
using OptiCli.Cms.Compat;
using OptiCli.Cms.Content;

namespace OptiCli.Cms.Operations;

/// <summary>Which media version to show, and how large.</summary>
/// <param name="Lang">The language branch, for media that has them; the master language when left out.</param>
/// <param name="Version"><c>published</c> (the default), <c>latest</c> or a version id, as for <see cref="ReadOperation"/>.</param>
/// <param name="MaxSize">The longest side in pixels; <see cref="ViewMediaOperation.LargestSize"/> when left out, and at most that.</param>
internal sealed record ViewMediaRequest(string? Lang = null, string? Version = null, int? MaxSize = null);

/// <summary>
/// A media file as an assistant can look at it: an image, shrunk where needed, or an SVG's markup. The bytes go to the
/// caller apart from the rest, which describes them.
/// </summary>
/// <param name="Ref">The version shown.</param>
/// <param name="MimeType">What <see cref="Image"/> is, or <c>image/svg+xml</c> for <see cref="Svg"/>.</param>
/// <param name="Width">Of <see cref="Image"/>; null for an SVG.</param>
/// <param name="Height">Of <see cref="Image"/>; null for an SVG.</param>
/// <param name="Original">The stored file, when <see cref="Image"/> isn't it.</param>
internal sealed record MediaView(
    string Ref,
    string Name,
    string MimeType,
    int? Width,
    int? Height,
    long Bytes,
    MediaOriginal? Original)
{
    /// <summary>The image to show, as <see cref="MimeType"/>; null for an SVG.</summary>
    [JsonIgnore]
    public byte[]? Image { get; init; }

    /// <summary>An SVG's markup, which is the site's data like any other text in content.</summary>
    [JsonIgnore]
    public string? Svg { get; init; }
}

/// <summary>The stored file, when what is shown was made from it: its format, size in pixels and in bytes.</summary>
internal sealed record MediaOriginal(string MimeType, int Width, int Height, long Bytes);

/// <summary>
/// Shows a media item's file: a JPEG, PNG, GIF or WebP image, scaled down with the site's own image service when it is
/// larger than an assistant takes in (<see cref="LargestSize"/> pixels, <see cref="LargestBytes"/>), or an SVG as its
/// markup. Read like <see cref="ReadOperation"/>: the version the caller asks for, which they may read.
/// </summary>
internal static class ViewMediaOperation
{
    /// <summary>The longest side: larger images are scaled down by the model anyway, at the cost of the tokens.</summary>
    public const int LargestSize = 1568;

    /// <summary>The smallest <see cref="ViewMediaRequest.MaxSize"/>.</summary>
    public const int SmallestSize = 64;

    /// <summary>The most bytes an image is shown with: well within what clients and models take per image.</summary>
    public const int LargestBytes = 1024 * 1024;

    /// <summary>Larger files aren't read: no image an editor needs to look at is this large.</summary>
    public const long LargestFile = 50L * 1024 * 1024;

    /// <summary>
    /// More pixels than this aren't decoded to shrink them: the image service holds them all in memory. 64 million is a
    /// 9000 by 7000 photo; a small file can claim far more.
    /// </summary>
    public const long LargestPixels = 64_000_000;

    /// <summary>The longest SVG markup shown.</summary>
    public const int LargestSvg = 256 * 1024;

    private const string Svg = "image/svg+xml";

    private const int Quality = 80;

    public static MediaView Run(CmsCall call, string reference, ViewMediaRequest body)
    {
        var maxSize = body.MaxSize ?? LargestSize;
        if (maxSize is < SmallestSize or > LargestSize)
        {
            throw AgentException.Usage($"maxSize must be between {SmallestSize} and {LargestSize}.");
        }

        var content = ReadOperation.Load(call, reference, new ReadRequest(body.Lang, body.Version));
        if (content is not MediaData media)
        {
            throw AgentException.Usage($"{content.ContentLink.ID} ('{content.Name}') is not media: only images (media items) can be shown.");
        }
        var blob = media.BinaryData
            ?? throw AgentException.NotFound($"{content.ContentLink.ID} ('{content.Name}') has no file.");
        // The blob store names the file by a GUID: only its extension is the uploaded file's.
        var extension = Path.GetExtension(blob.ID.AbsolutePath).ToLowerInvariant();
        var file = Read(blob, content);

        if (extension == ".svg")
        {
            if (file.Length > LargestSvg)
            {
                throw AgentException.Refused($"{content.ContentLink.ID} ('{content.Name}') is an SVG of {file.Length} bytes, too long to show.", OpenInCms(call));
            }
            return new MediaView(content.ContentLink.ToString(), content.Name, Svg, null, null, file.Length, null)
            {
                Svg = Encoding.UTF8.GetString(file),
            };
        }

        var header = ImageHeaders.Read(file)
            ?? throw AgentException.Usage(
                $"{content.ContentLink.ID} ('{content.Name}'{(extension.Length > 1 ? $", a {extension[1..]} file" : "")}) is not an image that can be shown: only JPEG, PNG, GIF and WebP images, and SVG as markup.",
                OpenInCms(call));
        if (Math.Max(header.Width, header.Height) <= maxSize && file.Length <= LargestBytes)
        {
            return new MediaView(content.ContentLink.ToString(), content.Name, header.MimeType, header.Width, header.Height, file.Length, null)
            {
                Image = file,
            };
        }

        var shrunk = Shrink(call, content, file, header, maxSize);
        return new MediaView(content.ContentLink.ToString(), content.Name, shrunk.MimeType, shrunk.Width, shrunk.Height, shrunk.Image.Length,
            new MediaOriginal(header.MimeType, header.Width, header.Height, file.Length))
        {
            Image = shrunk.Image,
        };
    }

    private sealed record Shrunk(string MimeType, int Width, int Height, byte[] Image);

    /// <summary>
    /// Scaled to fit <paramref name="maxSize"/>, as a JPEG from a JPEG and a PNG from the rest (an animated GIF's first
    /// frame); a PNG still above <see cref="LargestBytes"/> becomes a JPEG, and a JPEG that is too, smaller.
    /// </summary>
    private static Shrunk Shrink(CmsCall call, IContent content, byte[] file, ImageHeader header, int maxSize)
    {
        if ((long)header.Width * header.Height > LargestPixels)
        {
            throw AgentException.Refused(
                $"{content.ContentLink.ID} ('{content.Name}') is {header.Width} by {header.Height} pixels: too large to scale down here.", OpenInCms(call));
        }
        var resize = CmsImages.Resizer(call.Services)
            ?? throw AgentException.Refused(
                $"{content.ContentLink.ID} ('{content.Name}') is {header.Width} by {header.Height} pixels and {file.Length} bytes, larger than can be shown, and the site has no image service to scale it down (EPiServer.ImageLibrary.ImageSharp).",
                OpenInCms(call));

        var longest = Math.Max(header.Width, header.Height);
        var size = Math.Min(longest, maxSize);
        var mimeType = header.MimeType == ImageHeaders.Jpeg ? ImageHeaders.Jpeg : ImageHeaders.Png;
        while (true)
        {
            var zoom = (float)size / longest;
            byte[] image;
            try
            {
                image = resize(file, zoom, mimeType, Quality);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The image service can't read every file the header reader accepts: WebP before ImageSharp 2 (CMS 12.0
                // to 12.10), or a damaged file. Its message says which.
                throw AgentException.Refused(
                    $"The site's image service couldn't scale down {content.ContentLink.ID} ('{content.Name}', {header.MimeType}, {header.Width} by {header.Height} pixels): {e.Message}",
                    OpenInCms(call));
            }
            if (image.Length <= LargestBytes)
            {
                return new Shrunk(mimeType, Scaled(header.Width, zoom), Scaled(header.Height, zoom), image);
            }
            if (mimeType == ImageHeaders.Png)
            {
                mimeType = ImageHeaders.Jpeg;
            }
            else if (size > SmallestSize * 4)
            {
                size = size * 3 / 4;
            }
            else
            {
                throw AgentException.Refused(
                    $"{content.ContentLink.ID} ('{content.Name}') is still larger than can be shown after scaling it down.", OpenInCms(call));
            }
            call.Aborted.ThrowIfCancellationRequested();
        }
    }

    /// <summary>As the image services round a zoomed side; never below one pixel.</summary>
    private static int Scaled(int side, float zoom) => Math.Max(1, (int)Math.Round(side * zoom));

    private static byte[] Read(EPiServer.Framework.Blobs.Blob blob, IContent content)
    {
        using var stream = blob.OpenRead();
        if (stream.CanSeek && stream.Length > LargestFile)
        {
            throw AgentException.Refused($"{content.ContentLink.ID} ('{content.Name}') is a file of {stream.Length} bytes, too large to read here.");
        }
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (copy.Length + read > LargestFile)
            {
                throw AgentException.Refused($"{content.ContentLink.ID} ('{content.Name}') is a file of more than {LargestFile} bytes, too large to read here.");
            }
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }


    private static string OpenInCms(CmsCall call) =>
        call.ForCaller("Open the media in the CMS to look at it.", "Tell the user to open it in the CMS (the edit UI's media panel) to look at it.");
}
