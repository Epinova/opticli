using System.Text.Json.Nodes;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Output;

/// <summary>
/// Writes results and errors in the selected format and returns the process exit code.
/// JSON and JSON lines (success and error alike) go to stdout so a caller only has to parse one stream;
/// in text mode errors go to stderr.
/// </summary>
/// <remarks>
/// JSON lines: every item of a list result on its own line, then <c>{"meta": {...}}</c> as the last line only
/// when there is more to know (<c>next</c> or <c>warnings</c>), so a plain list stays a plain stream of items.
/// An error is the usual one-line error envelope (it has <c>"ok": false</c>, items never do).
/// </remarks>
public sealed class OutputWriter(OutputFormat format, TextWriter stdout, TextWriter stderr)
{
    public OutputFormat Format { get; } = format;

    public int Success(object? data, Meta meta)
    {
        if (Format == OutputFormat.Json)
        {
            stdout.WriteLine(JsonOutput.Serialize(Envelope.Success(data, meta)));
        }
        else if (Format == OutputFormat.JsonLines)
        {
            WriteLines(data, meta);
        }
        else
        {
            TextRenderer.Render(JsonOutput.ToNode(data), stdout);
            if (meta.Next is { } next)
            {
                stdout.WriteLine($"(more: add --cursor {next})");
            }
            foreach (var warning in meta.Warnings ?? [])
            {
                stderr.WriteLine($"warning: {warning}");
            }
        }
        return ExitCodes.Ok;
    }

    public int Failure(OptiCliException exception) => Failure(exception.Code, exception.Message, exception.Hint, exception.Details);

    public int Failure(ErrorCode code, string message, string? hint = null, object? details = null)
    {
        if (Format != OutputFormat.Text)
        {
            stdout.WriteLine(JsonOutput.Serialize(Envelope.Failure(code, message, hint, details)));
        }
        else
        {
            stderr.WriteLine($"error ({ExitCodes.Name(code)}): {message}");
            if (details is not null)
            {
                TextRenderer.Render(JsonOutput.ToNode(details), stderr);
            }
            if (hint is not null)
            {
                stderr.WriteLine($"hint: {hint}");
            }
        }
        return ExitCodes.For(code);
    }

    private void WriteLines(object? data, Meta meta)
    {
        // Commands only offer --jsonl when their data is a list; anything else is written as one line.
        if (JsonOutput.ToNode(data) is JsonArray items)
        {
            foreach (var item in items)
            {
                stdout.WriteLine(item?.ToJsonString(JsonOutput.Options) ?? "null");
            }
        }
        else
        {
            stdout.WriteLine(JsonOutput.Serialize(data));
        }
        if (meta.Next is not null || meta.Warnings is { Count: > 0 })
        {
            stdout.WriteLine(JsonOutput.Serialize(new { meta }));
        }
    }
}
