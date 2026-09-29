using OptiCli.Core.Errors;

namespace OptiCli.Core.Output;

public enum OutputFormat
{
    Json,

    /// <summary>One JSON value per line: each list item, then a <c>{"meta": ...}</c> line only when there is a next page or warnings.</summary>
    JsonLines,
    Text,
}

public static class OutputFormats
{
    /// <summary>
    /// Compact JSON when stdout is redirected (an agent or a pipe), tables on a terminal;
    /// <c>--json</c> / <c>--jsonl</c> / <c>--text</c> override.
    /// </summary>
    public static OutputFormat Select(bool json, bool jsonLines, bool text, bool outputRedirected)
    {
        if ((json ? 1 : 0) + (jsonLines ? 1 : 0) + (text ? 1 : 0) > 1)
        {
            throw new UsageException("Pass only one of --json, --jsonl and --text.");
        }
        return json ? OutputFormat.Json
            : jsonLines ? OutputFormat.JsonLines
            : text ? OutputFormat.Text
            : outputRedirected ? OutputFormat.Json
            : OutputFormat.Text;
    }
}
