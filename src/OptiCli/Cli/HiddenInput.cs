using System.Text;

namespace OptiCli.Cli;

/// <summary>Reads a line at a terminal without showing what is typed (a password).</summary>
internal static class HiddenInput
{
    /// <param name="question">Written to stderr first.</param>
    public static string Read(string question)
    {
        Console.Error.Write(question);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return text.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}
