using System.Globalization;

namespace OptiCli.Core.Data;

/// <summary>
/// <c>IN (...)</c> lists of ids and GUIDs. They are formatted from <see cref="int"/> and <see cref="Guid"/>
/// values, never from user text, so inlining them is safe, and it works on databases whose
/// compatibility level predates <c>STRING_SPLIT</c>/<c>OPENJSON</c>.
/// </summary>
internal static class SqlLists
{
    private const int ChunkSize = 1000;

    public static IEnumerable<string> Ints(IEnumerable<int> values) =>
        values.Distinct().Chunk(ChunkSize).Select(chunk => string.Join(",", chunk.Select(v => v.ToString(CultureInfo.InvariantCulture))));

    public static IEnumerable<string> Guids(IEnumerable<Guid> values) =>
        values.Distinct().Chunk(ChunkSize).Select(chunk => string.Join(",", chunk.Select(g => $"'{g:D}'")));
}
