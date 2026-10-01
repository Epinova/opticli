namespace OptiCli.Cli;

/// <summary>A yes/no question to a person at a terminal, for writes that can't be undone. Agents get a <c>conflict</c> instead.</summary>
internal static class ConfirmPrompt
{
    /// <returns>True only for an explicit yes.</returns>
    public static bool Ask(string message, string question)
    {
        Console.Error.WriteLine(message);
        Console.Error.Write($"{question} [y/N]: ");
        var answer = Console.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
