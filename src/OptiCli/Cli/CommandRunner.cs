using System.CommandLine;
using OptiCli.Core;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;

namespace OptiCli.Cli;

/// <summary>
/// Wraps every command body: builds the context, writes the envelope, and turns exceptions into
/// error codes, so a command only returns data or throws.
/// </summary>
internal static class CommandRunner
{
    public static void SetHandler(Command command, GlobalOptions options, Func<CliContext, CancellationToken, Task<CommandResult>> body)
    {
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = CreateWriter(parse, options, out var formatError);
            if (formatError is not null)
            {
                return writer.Failure(formatError);
            }

            var environment = OptiCliEnvironment.FromProcess();
            for (var attempt = 0; ; attempt++)
            {
                var context = new CliContext(parse, options, environment);
                try
                {
                    var result = await body(context, cancellationToken);
                    var warnings = (result.Warnings ?? []).Concat(context.DatabaseWarnings).ToList();
                    if (result.Raw is { } raw)
                    {
                        WriteWarnings(context.DatabaseWarnings);
                        Console.Out.Write(raw);
                        return ExitCodes.Ok;
                    }
                    if (result.Text is { } text && writer.Format == OutputFormat.Text)
                    {
                        WriteWarnings(context.DatabaseWarnings);
                        Console.Out.Write(text);
                        return ExitCodes.Ok;
                    }
                    var data = writer.Format == OutputFormat.JsonLines && result.Lines is not null ? result.Lines : result.Data;
                    return writer.Success(data, new Meta(result.Source, ToolInfo.Version, result.Next, warnings.Count > 0 ? warnings : null, context.DatabaseMeta));
                }
                catch (NeedsSelectionException ex) when (attempt == 0 && DatabasePrompt.CanAsk)
                {
                    // A person at a terminal answers here and the command runs again; agents get the error and ask the user.
                    if (!DatabasePrompt.AskAndSave(context, ex.Message))
                    {
                        return writer.Failure(ex);
                    }
                }
                catch (OptiCliException ex)
                {
                    return writer.Failure(ex);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return writer.Failure(ErrorCode.NotFound, ex.Message, "Check the path.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return writer.Failure(ErrorCode.Usage, ex.Message, "Check that the file exists and that you can read (or write) it.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return writer.Failure(ErrorCode.Internal, $"{ex.GetType().Name}: {ex.Message}", "This is a bug in opticli; please report it with the command you ran.");
                }
            }
        });
    }

    private static void WriteWarnings(IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
    }

    /// <summary>Always returns a writer; on conflicting format flags it falls back to JSON and reports the error.</summary>
    public static OutputWriter CreateWriter(ParseResult parse, GlobalOptions options, out UsageException? error)
    {
        error = null;
        OutputFormat format;
        try
        {
            // --jsonl only exists on list commands; elsewhere it is an unknown token, reported as a parse error.
            var jsonLines = parse.GetResult(options.JsonLines) is not null && parse.GetValue(options.JsonLines);
            format = OutputFormats.Select(parse.GetValue(options.Json), jsonLines, parse.GetValue(options.Text), Console.IsOutputRedirected);
        }
        catch (UsageException ex)
        {
            error = ex;
            format = OutputFormat.Json;
        }
        return new OutputWriter(format, Console.Out, Console.Error);
    }
}
