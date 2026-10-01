using System.Text.Json;
using OptiCli.Core.Output;
using OptiCli.Protocol;

namespace OptiCli.Cli;

/// <summary>
/// Asks a person at a terminal whether a publish should also put live the unpublished changes someone else saved, or a
/// discard delete them.
/// Agents never see this: their stdin/stdout aren't terminals, so they get the <c>conflict</c> with
/// <c>details.reason: "pendingDraft"</c> instead, and ask the user.
/// </summary>
internal static class PendingDraftPrompt
{
    private const int MaxValueLength = 50;

    /// <param name="message">The agent's description of what would be published (or discarded).</param>
    /// <param name="question">What to ask, e.g. "Publish these changes too?".</param>
    /// <returns>True only for an explicit yes.</returns>
    public static bool Ask(string message, PendingDraft draft, string question)
    {
        var error = Console.Error;
        error.WriteLine(message);
        error.WriteLine();
        if (draft.Changes.Count > 0)
        {
            TextRenderer.Render(JsonOutput.ToNode(draft.Changes.Select(c => new { c.Property, Published = Short(c.Before), Draft = Short(c.After) })), error);
            error.WriteLine();
        }
        error.Write($"{question} [y/N]: ");
        var answer = Console.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static string Short(JsonElement? value)
    {
        if (value is not { } element)
        {
            return "(empty)";
        }
        var text = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= MaxValueLength ? text : text[..(MaxValueLength - 3)] + "...";
    }
}
