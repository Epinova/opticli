using System.Text;

namespace OptiCli.Cli;

/// <summary>
/// The person at the terminal, for the sealed apply: whether there is one (stdin, stdout and stderr all terminals), and
/// asking them things on stderr. An interface so tests can stand in for the person; nothing an agent can set (an option, an
/// environment variable, a config entry) chooses another one.
/// </summary>
internal interface ITerminal
{
    /// <summary>stdin, stdout and stderr are all terminals.</summary>
    bool IsInteractive { get; }

    void WriteLine(string text = "");

    void Write(string text);

    /// <returns>Null at the end of input.</returns>
    string? ReadLine();

    /// <summary>A line read without showing what is typed (a password).</summary>
    string ReadHidden(string question);
}

internal sealed class ConsoleTerminal : ITerminal
{
    public bool IsInteractive => DatabasePrompt.CanAsk;

    public void WriteLine(string text = "") => Console.Error.WriteLine(text);

    public void Write(string text) => Console.Error.Write(text);

    public string? ReadLine() => Console.ReadLine();

    public string ReadHidden(string question) => HiddenInput.Read(question);
}

internal static class Printable
{
    /// <summary>
    /// <paramref name="text"/> with control characters (escape sequences, carriage returns, C1 codes) shown as <c>?</c>, so text
    /// from a plan or a seal can't move the cursor or clear what the user reads before they confirm.
    /// </summary>
    public static string Of(string? text)
    {
        if (text is null)
        {
            return "";
        }
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            result.Append(char.IsControl(c) || c is '\u2028' or '\u2029' or '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069' ? '?' : c);
        }
        return result.ToString();
    }
}
