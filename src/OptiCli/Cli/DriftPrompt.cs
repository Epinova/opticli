using OptiCli.Commands;
using OptiCli.Core.Drift;
using OptiCli.Core.Output;
using OptiCli.Protocol;

namespace OptiCli.Cli;

/// <summary>
/// Asks a person at a terminal whether to write although this build and the shared database differ. Agents never see
/// this: they get the <c>drift</c> error with the report in its details, and ask the user.
/// </summary>
internal static class DriftPrompt
{
    /// <returns>True only for an explicit yes.</returns>
    public static bool Ask(DriftReport report)
    {
        var error = Console.Error;
        error.WriteLine($"This build and the shared database differ ({DriftText.Short(report.Ahead)}). A write runs this build's code (models, validators, event handlers) against content the deployed site serves.");
        error.WriteLine(DriftText.Advice(report.Ahead));
        error.WriteLine();
        TextRenderer.Render(JsonOutput.ToNode(DriftCommand.Rows(report)), error);
        error.WriteLine();
        error.Write($"Write with this build anyway (drift {report.Fingerprint})? [y/N]: ");
        var answer = Console.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
