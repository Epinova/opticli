using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using OptiCli.Mcp.Integration.Support;
using static OptiCli.Mcp.Integration.WritingTests;

namespace OptiCli.Mcp.Integration;

/// <summary>Showing images to the assistant: as they are stored, or scaled down by the site's own image service.</summary>
[Collection(McpSiteCollection.Name)]
public sealed class MediaTests(SharedSessions sessions)
{
    [McpSiteFact]
    public async Task A_large_image_is_shown_scaled_down_by_the_sites_image_service()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = $"opticli-mcp-it {Guid.NewGuid():N}", parent = start })).GetProperty("content"));
        try
        {
            var png = Gradient(2400, 1200);
            var uploaded = await editor.OkAsync("upload_media", new { fileName = "gradient.png", data = Convert.ToBase64String(png), forContent = page });
            var media = Id(uploaded.GetProperty("content"));

            var (view, image) = await ViewAsync(editor, new { reference = media, version = "latest" });
            Assert.Equal(("image/png", 1568, 784), (image.MimeType, view.GetProperty("width").GetInt32(), view.GetProperty("height").GetInt32()));
            Assert.Equal((1568, 784), PngSize(image.DecodedData.Span));
            var original = view.GetProperty("original");
            Assert.Equal(("image/png", 2400, 1200, png.Length), (original.GetProperty("mimeType").GetString(), original.GetProperty("width").GetInt32(), original.GetProperty("height").GetInt32(), original.GetProperty("bytes").GetInt32()));

            var (small, smallImage) = await ViewAsync(editor, new { reference = media, version = "latest", maxSize = 200 });
            Assert.Equal((200, 100), PngSize(smallImage.DecodedData.Span));
            Assert.Equal(200, small.GetProperty("width").GetInt32());

            var notMedia = await editor.ErrorAsync("view_media", new { reference = page });
            Assert.Equal("usage", notMedia.GetProperty("code").GetString());
            Assert.Contains("is not media", notMedia.GetProperty("message").GetString());
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task The_sites_own_images_can_be_seen()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var images = (await editor.OkAsync("find_content", new { name = ".", type = "ImageFile", limit = 20 })).GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(images);

        foreach (var item in images)
        {
            var (view, image) = await ViewAsync(editor, new { reference = Id(item) });
            Assert.Contains(image.MimeType, new[] { "image/jpeg", "image/png", "image/gif", "image/webp" });
            Assert.InRange(Math.Max(view.GetProperty("width").GetInt32(), view.GetProperty("height").GetInt32()), 1, 1568);
            Assert.InRange(image.DecodedData.Length, 1, 1024 * 1024);
        }
    }

    private static async Task<(JsonElement View, ImageContentBlock Image)> ViewAsync(McpSession session, object arguments)
    {
        var result = await session.CallAsync("view_media", arguments);
        Assert.False(result.IsError, $"view_media failed: {result.Text}");
        Assert.Equal(2, result.Content.Count);
        var view = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement.Clone();
        return (view, Assert.IsType<ImageContentBlock>(result.Content[1]));
    }

    private static (int Width, int Height) PngSize(ReadOnlySpan<byte> png)
    {
        Assert.True(png[1..4].SequenceEqual("PNG"u8), "not a PNG");
        return ((int)BinaryPrimitives.ReadUInt32BigEndian(png[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(png[20..]));
    }

    /// <summary>An RGB PNG with a colour gradient: a real image to decode, whose file stays small.</summary>
    private static byte[] Gradient(int width, int height)
    {
        var pixels = new MemoryStream();
        using (var deflate = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    (row[1 + x * 3], row[2 + x * 3], row[3 + x * 3]) = ((byte)(x * 255 / width), (byte)(y * 255 / height), 128);
                }
                deflate.Write(row);
            }
        }
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9]) = (8, 2);

        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", pixels.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }
}
