using OptiCli.Commands;
using OptiCli.Core.Configuration;
using OptiCli.Core.Output;

namespace OptiCli.Cli;

/// <summary>
/// Asks a person at a terminal which database is the project's development database, and saves the answer.
/// Agents never see this: their stdin/stdout aren't terminals, so they get the <c>needs_selection</c> error instead.
/// </summary>
internal static class DatabasePrompt
{
    public static bool CanAsk => !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected;

    /// <returns>False when there is nothing to choose from or the person cancelled.</returns>
    public static bool AskAndSave(CliContext context, string? reason)
    {
        var resolution = context.ResolveConnection();
        var selectable = resolution.Selectable;
        if (selectable.Count == 0)
        {
            return false;
        }

        var error = Console.Error;
        if (reason is not null)
        {
            error.WriteLine(reason);
            error.WriteLine();
        }
        TextRenderer.Render(JsonOutput.ToNode(resolution.Choices().Select(c => new { c.N, c.Server, c.Database, c.Local, c.From })), error);
        error.WriteLine();

        while (true)
        {
            error.Write($"Which one is this project's development database? [1-{selectable.Count}, Enter to cancel]: ");
            var answer = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer))
            {
                return false;
            }
            if (int.TryParse(answer, out var n) && n >= 1 && n <= selectable.Count)
            {
                var chosen = selectable[n - 1];
                Save(context, chosen, SavedDatabase.ViaPrompt);
                error.WriteLine($"Saved '{chosen.Database}' on '{chosen.Server}' as the development database (change it with `opticli db use`).");
                if (chosen.IsLocal != true)
                {
                    error.WriteLine(DbCommand.RemoteNote);
                }
                error.WriteLine();
                return true;
            }
        }
    }

    public static SavedDatabase Save(CliContext context, ConnectionCandidate candidate, string via)
    {
        var saved = SavedDatabase.From(candidate, via, DateTimeOffset.UtcNow);
        UserConfig.SaveDatabase(context.Environment.UserConfigFile, context.Project.Directory, saved);
        return saved;
    }
}
