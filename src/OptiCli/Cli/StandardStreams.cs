using System.Text;

namespace OptiCli.Cli;

/// <summary>
/// UTF-8 on redirected stdin, stdout and stderr. What reads opticli's output or feeds it a plan (the npm package, a
/// script, an agent, CI) speaks UTF-8, but on Windows .NET encodes a redirected stream in the console's code page:
/// <c>æ</c> in a content name, or <c>→</c> in a description, came out as other bytes, or as a control character that
/// breaks the JSON, and a plan piped to <c>apply -</c> was read the same wrong way. A real console is left alone
/// (.NET writes Unicode to it), and so is a stream already in UTF-8 (Linux and macOS) or one a test swapped in.
/// </summary>
internal static class StandardStreams
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void UseUtf8WhenRedirected()
    {
        if (Console.IsOutputRedirected && NeedsUtf8(Console.Out.Encoding))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true });
        }
        if (Console.IsErrorRedirected && NeedsUtf8(Console.Error.Encoding))
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true });
        }
        if (Console.IsInputRedirected && NeedsUtf8(Console.InputEncoding))
        {
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), Utf8));
        }
    }

    /// <summary>
    /// A legacy code page (437, 850, 1252, ...). Not UTF-8, and not UTF-16, which is what a <see cref="StringWriter"/>
    /// reports when a test captures the console.
    /// </summary>
    internal static bool NeedsUtf8(Encoding encoding) => encoding.CodePage is not (65001 or 1200 or 1201);
}
