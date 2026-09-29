namespace OptiCli.Core.Serve;

public static class LogTail
{
    /// <summary>The last <paramref name="count"/> lines of a log the site may still be writing; empty when it doesn't exist.</summary>
    public static IReadOnlyList<string> Read(string path, int count)
    {
        if (count <= 0 || !File.Exists(path))
        {
            return [];
        }
        var lines = new Queue<string>(count);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (lines.Count == count)
            {
                lines.Dequeue();
            }
            lines.Enqueue(line);
        }
        return lines.ToList();
    }
}
