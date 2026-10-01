using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Cli;

/// <summary>Arguments and options shared by the write commands.</summary>
internal sealed class WriteOptions
{
    public Option<bool> DryRun { get; } = new("--dry-run")
    {
        Description = "Validate and show the before/after diff without saving anything.",
    };

    public Option<bool> Publish { get; } = new("--publish")
    {
        Description = "Publish the new version. Default: save it as a draft, which changes nothing live.",
    };

    public Argument<string[]> Properties { get; } = new("properties")
    {
        Description = $"Property values: {PropertyArguments.Syntax}.",
        Arity = ArgumentArity.ZeroOrMore,
    };

    public Option<string?> Values { get; } = new("--values")
    {
        Description = """Property values as a JSON object, merged over the Prop=value arguments; for ContentAreas ([{"ref":"123"}]), links, lists and local blocks.""",
        HelpName = "json",
    };

    public Option<string?> BaseVersion { get; } = new("--base-version")
    {
        Description = "Version the change is based on (id or 123_456). Default: the latest version, read just before saving; if a newer one exists by then the write fails with a conflict (exit 5).",
        HelpName = "version",
    };

    public Option<bool> Force { get; } = new("--force")
    {
        Description = "Skip the concurrency check (save even if someone saved a newer version meanwhile).",
    };

    public Option<bool> IncludeDraft { get; } = new("--include-draft")
    {
        Description = "Also publish unpublished changes someone else saved after the published version. Without it such a publish asks on a terminal, and elsewhere fails with a conflict (exit 5) that lists them.",
    };

    public void AddCommon(Command command, bool publish = true)
    {
        command.Options.Add(DryRun);
        if (publish)
        {
            command.Options.Add(Publish);
        }
    }

    /// <summary>For commands that publish existing content, which may hold someone else's draft.</summary>
    public void AddIncludeDraft(Command command) => command.Options.Add(IncludeDraft);

    public void AddProperties(Command command)
    {
        command.Arguments.Add(Properties);
        command.Options.Add(Values);
    }

    public void AddConcurrency(Command command)
    {
        command.Options.Add(BaseVersion);
        command.Options.Add(Force);
    }

    /// <returns>Null when no properties were given.</returns>
    public JsonObject? ParseProperties(CliContext context)
    {
        var properties = PropertyArguments.Parse(context.Parse.GetValue(Properties) ?? [], context.Parse.GetValue(Values), context.Environment.CurrentDirectory);
        return properties.Count == 0 ? null : properties;
    }

    public int? ParseBaseVersion(CliContext context)
    {
        var value = context.Parse.GetValue(BaseVersion);
        if (value is null)
        {
            return null;
        }
        if (context.Parse.GetValue(Force))
        {
            throw new UsageException("Pass either --base-version or --force, not both.");
        }
        return ParseVersion(value, "--base-version");
    }

    /// <summary>A version id, as <c>456</c> or <c>123_456</c>.</summary>
    public static int ParseVersion(string value, string option)
    {
        var text = value.Contains('_') ? value[(value.LastIndexOf('_') + 1)..] : value;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0
            ? version
            : throw new UsageException($"{option} '{value}' is not a version (use the id, or 123_456 as shown by `opticli versions`).");
    }

    public static CommandResult Result(WriteOutcome outcome) =>
        new(outcome.Output, Warnings: outcome.Warnings.Count > 0 ? outcome.Warnings : null, Source: outcome.Source);
}
