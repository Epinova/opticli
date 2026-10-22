using System.Buffers.Binary;
using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Content;

public class ImageHeadersTests
{
    [Fact]
    public void Reads_a_png() => Assert.Equal(new ImageHeader("image/png", 1920, 1080), ImageHeaders.Read(Png(1920, 1080)));

    [Fact]
    public void Reads_both_gif_versions()
    {
        Assert.Equal(new ImageHeader("image/gif", 320, 200), ImageHeaders.Read(Gif("GIF89a", 320, 200)));
        Assert.Equal(new ImageHeader("image/gif", 16, 16), ImageHeaders.Read(Gif("GIF87a", 16, 16)));
    }

    [Theory]
    [InlineData(0xC0)]
    [InlineData(0xC2)]
    public void Reads_a_jpeg_after_the_segments_before_its_frame(byte frame) =>
        Assert.Equal(new ImageHeader("image/jpeg", 4000, 3000), ImageHeaders.Read(Jpeg(4000, 3000, frame)));

    [Fact]
    public void Skips_a_jpegs_huffman_table_and_fill_bytes()
    {
        // DHT (C4) is in the start-of-frame range but isn't one.
        byte[] table = [0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00];
        byte[] fill = [0xFF];
        var jpeg = Jpeg(800, 600);
        var bytes = jpeg[..2].Concat(table).Concat(fill).Concat(jpeg[2..]).ToArray();

        Assert.Equal(new ImageHeader("image/jpeg", 800, 600), ImageHeaders.Read(bytes));
    }

    [Fact]
    public void Reads_lossy_lossless_and_extended_webp()
    {
        Assert.Equal(new ImageHeader("image/webp", 1024, 768), ImageHeaders.Read(WebP("VP8 ", b =>
        {
            b[23] = 0x9D;
            b[24] = 0x01;
            b[25] = 0x2A;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 1024);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), 768);
        })));
        Assert.Equal(new ImageHeader("image/webp", 500, 300), ImageHeaders.Read(WebP("VP8L", b =>
        {
            b[20] = 0x2F;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(21), 499u | (299u << 14));
        })));
        Assert.Equal(new ImageHeader("image/webp", 6000, 4000), ImageHeaders.Read(WebP("VP8X", b =>
        {
            (b[24], b[25], b[26]) = ((byte)(5999 & 0xFF), (byte)(5999 >> 8), 0);
            (b[27], b[28], b[29]) = ((byte)(3999 & 0xFF), (byte)(3999 >> 8), 0);
        })));
    }

    [Fact]
    public void Anything_else_or_a_cut_short_header_is_no_image()
    {
        Assert.Null(ImageHeaders.Read("%PDF-1.7\n"u8));
        Assert.Null(ImageHeaders.Read("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Null(ImageHeaders.Read([]));
        Assert.Null(ImageHeaders.Read(Png(10, 10)[..20]));
        Assert.Null(ImageHeaders.Read(Jpeg(10, 10)[..10]));
        // A segment length that points past the end.
        Assert.Null(ImageHeaders.Read([0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xFF]));
        Assert.Null(ImageHeaders.Read(Png(0, 10)));
    }

    internal static byte[] Png(uint width, uint height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static byte[] Gif(string version, ushort width, ushort height)
    {
        var bytes = new byte[13];
        System.Text.Encoding.ASCII.GetBytes(version).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), height);
        return bytes;
    }

    /// <summary>Start of image, a JFIF segment, then a frame header (baseline C0 or progressive C2).</summary>
    internal static byte[] Jpeg(ushort width, ushort height, byte frame = 0xC0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        bytes.AddRange("JFIF\0"u8.ToArray());
        bytes.AddRange(new byte[9]);
        bytes.AddRange([0xFF, frame, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        bytes.AddRange(new byte[9]);
        return [.. bytes];
    }

    private static byte[] WebP(string chunk, Action<byte[]> frame)
    {
        var bytes = new byte[40];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 32);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(chunk).CopyTo(bytes, 12);
        frame(bytes);
        return bytes;
    }
}
