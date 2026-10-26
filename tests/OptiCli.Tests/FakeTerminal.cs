using OptiCli.Cli;

namespace OptiCli.Tests;

/// <summary>The person at the terminal: answers from <see cref="Lines"/>, a password from <see cref="Hidden"/>.</summary>
internal sealed class FakeTerminal : ITerminal
{
    private readonly System.Text.StringBuilder _output = new();

    public bool Interactive { get; set; } = true;

    public Queue<string> Lines { get; } = new();

    public Queue<string> Hidden { get; } = new();

    public string Output => _output.ToString();

    public bool IsInteractive => Interactive;

    public void WriteLine(string text = "") => _output.AppendLine(text);

    public void Write(string text) => _output.Append(text);

    public string? ReadLine() => Lines.Count > 0 ? Lines.Dequeue() : null;

    public string ReadHidden(string question)
    {
        _output.Append(question);
        return Hidden.Count > 0 ? Hidden.Dequeue() : "";
    }
}
