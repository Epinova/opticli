using System.Runtime.CompilerServices;
using EPiServer.ImageLibrary;
using Microsoft.Extensions.DependencyInjection;

namespace OptiCli.Cms.Compat;

/// <summary>
/// The CMS's own image service, which makes the edit UI's thumbnails: here it shrinks images. CMS 12 before 12.17 has it
/// built in; CMS 12.17 and newer and CMS 13 get it from <c>EPiServer.ImageLibrary.ImageSharp</c>, which the
/// <c>EPiServer.CMS</c> package brings. A site may have none, and on CMS 13 the assembly with its interface may then be
/// missing as well: the agent compiles against it but doesn't ship it.
/// </summary>
internal static class CmsImages
{
    /// <summary>A resizer for the site, or null if it has no image service.</summary>
    public static Func<byte[], float, string, int, byte[]>? Resizer(IServiceProvider services)
    {
        try
        {
            return Resolve(services);
        }
        catch (FileNotFoundException)
        {
            // CMS 13 without EPiServer.ImageLibrary: nothing of it can be loaded, so nothing of it is used.
            return null;
        }
    }

    /// <summary>Kept apart, so a missing assembly fails here, where it is caught, and not in the caller's compilation.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<byte[], float, string, int, byte[]>? Resolve(IServiceProvider services) =>
        services.GetService<IImageService>() is { } images ? (image, zoom, mimeType, quality) => Render(images, image, zoom, mimeType, quality) : null;

    /// <summary>
    /// <paramref name="image"/> scaled by <paramref name="zoom"/> and saved as <paramref name="mimeType"/> (JPEG with
    /// <paramref name="quality"/>, or PNG). Every CMS 12 and 13 image service scales by the zoom factor and writes both.
    /// </summary>
    private static byte[] Render(IImageService images, byte[] image, float zoom, string mimeType, int quality)
    {
        using var input = new MemoryStream(image, writable: false);
        using var output = new MemoryStream();
        // Operations is null by default on CMS 12.0, and the service enumerates it.
        images.RenderImage(input, output, new RenderRequest { Operations = [], MimeType = mimeType, ZoomFactor = zoom, Quality = quality });
        return output.ToArray();
    }
}
