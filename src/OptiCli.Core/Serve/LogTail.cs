using System.Text;

namespace OptiCli.Core.Serve;

public static class LogTail
{
    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// The last <paramref name="count"/> lines of a log the site may still be writing; empty when it doesn't exist.
    /// Reads backwards from the end, so a long log costs no more than its tail.
    /// </summary>
    public static IReadOnlyList<string> Read(string path, int count)
    {
        if (count <= 0 || !File.Exists(path))
        {
            return [];
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var start = StartOfLastLines(stream, length, count);

        // '\n' is never part of a multi-byte UTF-8 sequence, so decoding from just after one is safe.
        stream.Position = start;
        var bytes = new byte[length - start];
        stream.ReadExactly(bytes);
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return lines.Count > count ? lines[^count..] : lines;
    }

    /// <summary>The offset where the last <paramref name="count"/> lines begin (0 when the file has no more than that).</summary>
    private static long StartOfLastLines(FileStream stream, long length, int count)
    {
        var buffer = new byte[ChunkBytes];
        var position = length;
        var newlines = 0;
        // A final line break ends the last line rather than starting an empty one.
        var skipFinal = true;
        while (position > 0)
        {
            var size = (int)Math.Min(ChunkBytes, position);
            position -= size;
            stream.Position = position;
            stream.ReadExactly(buffer, 0, size);
            for (var i = size - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n')
                {
                    skipFinal = false;
                    continue;
                }
                if (skipFinal)
                {
                    skipFinal = false;
                    continue;
                }
                if (++newlines == count)
                {
                    return position + i + 1;
                }
            }
        }
        return 0;
    }
}
