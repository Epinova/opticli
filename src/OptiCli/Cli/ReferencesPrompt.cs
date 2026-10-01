using OptiCli.Core.Output;
using OptiCli.Core.Queries;

namespace OptiCli.Cli;

/// <summary>
/// Asks a person at a terminal whether to delete content that other content references. Agents never see this: they get
/// the <c>conflict</c> with <c>details.reason: "referenced"</c> instead, and ask the user.
/// </summary>
internal static class ReferencesPrompt
{
    /// <returns>True only for an explicit yes.</returns>
    public static bool Ask(string message, IncomingReferences found)
    {
        var error = Console.Error;
        error.WriteLine(message);
        error.WriteLine();
        TextRenderer.Render(JsonOutput.ToNode(found.References.Select(r => new { r.From, r.Name, r.Property, References = r.To })), error);
        if (found.Count > found.References.Count)
        {
            error.WriteLine($"... and {found.Count - found.References.Count} more (opticli where-used shows them).");
        }
        error.WriteLine();
        error.Write("Delete it anyway? [y/N]: ");
        var answer = Console.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
