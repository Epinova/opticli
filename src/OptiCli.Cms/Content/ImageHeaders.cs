using System.Buffers.Binary;

namespace OptiCli.Cms.Content;

/// <summary>An image's format, as its own bytes say, and its size in pixels.</summary>
internal sealed record ImageHeader(string MimeType, int Width, int Height);

/// <summary>
/// Reads the format and size of a JPEG, PNG, GIF or WebP file from its header: the formats an assistant can be shown as
/// they are. The bytes decide, not the file name or the media type the site maps its extension to.
/// </summary>
internal static class ImageHeaders
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Gif = "image/gif";
    public const string WebP = "image/webp";

    /// <summary>Null when the bytes are none of the four, or their header is cut short or damaged.</summary>
    public static ImageHeader? Read(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return ReadPng(bytes) ?? ReadGif(bytes) ?? ReadJpeg(bytes) ?? ReadWebP(bytes);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A header that claims more than the file has.
            return null;
        }
    }

    private static ImageHeader? ReadPng(ReadOnlySpan<byte> b) =>
        b.Length >= 24 && b[..8].SequenceEqual((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]) && b[12..16].SequenceEqual("IHDR"u8)
            ? Sized(Png, BinaryPrimitives.ReadUInt32BigEndian(b[16..]), BinaryPrimitives.ReadUInt32BigEndian(b[20..]))
            : null;

    private static ImageHeader? ReadGif(ReadOnlySpan<byte> b) =>
        b.Length >= 10 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))
            ? Sized(Gif, BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]))
            : null;

    /// <summary>The size is in the first start-of-frame segment; the segments before it are skipped by their lengths.</summary>
    private static ImageHeader? ReadJpeg(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8)
        {
            return null;
        }
        var i = 2;
        while (i + 1 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                return null;
            }
            var marker = b[i + 1];
            if (marker == 0xFF)
            {
                // Fill byte before a marker.
                i++;
                continue;
            }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                // Markers without a length.
                i += 2;
                continue;
            }
            if (marker is 0xD9 or 0xDA)
            {
                // End of image, or the scan began without a frame header.
                return null;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            // Start of frame: C0-CF, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return Sized(Jpeg, BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));
            }
            i += 2 + length;
        }
        return null;
    }

    /// <summary>Lossy (<c>VP8 </c>), lossless (<c>VP8L</c>) and extended (<c>VP8X</c>, animation and alpha) files.</summary>
    private static ImageHeader? ReadWebP(ReadOnlySpan<byte> b)
    {
        if (b.Length < 30 || !b[..4].SequenceEqual("RIFF"u8) || !b[8..12].SequenceEqual("WEBP"u8))
        {
            return null;
        }
        var chunk = b[12..16];
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // The frame tag (3 bytes), the start code 9D 01 2A, then 14-bit width and height.
            return b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A
                ? Sized(WebP, BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFFu, BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFFu)
                : null;
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            // The signature 2F, then width - 1 and height - 1 in 14 bits each.
            if (b[20] != 0x2F)
            {
                return null;
            }
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
            return Sized(WebP, (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8X"u8))
        {
            // Flags and reserved bytes, then the canvas width - 1 and height - 1 in 24 bits each.
            return Sized(WebP, Uint24(b[24..]) + 1, Uint24(b[27..]) + 1);
        }
        return null;
    }

    private static uint Uint24(ReadOnlySpan<byte> b) => b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16);

    private static ImageHeader? Sized(string mimeType, uint width, uint height) =>
        width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue ? new ImageHeader(mimeType, (int)width, (int)height) : null;
}
