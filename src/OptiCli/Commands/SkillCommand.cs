using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Skills;

namespace OptiCli.Commands;

/// <summary>Installs or prints the Claude Code skill that teaches agents to use opticli.</summary>
internal static class SkillCommand
{
    public static Command Create(GlobalOptions options)
    {
        var skill = new Command("skill", "Install or print the Claude Code skill that teaches coding agents to use opticli.");
        skill.Subcommands.Add(Install(options));
        skill.Subcommands.Add(Print(options));
        return skill;
    }

    /// <summary>
    /// The repository whose <c>.claude/skills/</c> a repository-scoped skill goes in: the git repository around
    /// the working directory, else the site's solution directory.
    /// </summary>
    public static string? RepositoryRoot(CliContext context) =>
        SkillLocations.FindRepositoryRoot(context.Environment.CurrentDirectory) ?? context.TryGetProject(out _)?.SourceRoot;

    private static Command Install(GlobalOptions options)
    {
        var repo = new Option<bool>("--repo")
        {
            Description = "Install into the repository's .claude/skills/opticli/ (the git repository around the working directory, shared through git) instead of ~/.claude/skills/opticli/.",
        };
        var force = new Option<bool>("--force") { Description = "Replace an installed copy with local edits, or one without an install record (installed by opticli before 0.4)." };
        var command = new Command("install", """
            Install the skill bundled with this opticli for Claude Code (SKILL.md plus reference.md).
            Goes to ~/.claude/skills/opticli/, or with --repo to <repository>/.claude/skills/opticli/. A copy this command
            installed is replaced when its files are unedited; edited files (or a copy without .opticli-install.json, from
            opticli before 0.4) are only replaced with --force. `opticli doctor` warns when an installed copy was written
            for an older opticli.
            Example: opticli skill install --repo
            """);
        command.Options.Add(repo);
        command.Options.Add(force);

        CommandRunner.SetHandler(command, options, (context, _) =>
        {
            string directory;
            if (context.Parse.GetValue(repo))
            {
                var root = RepositoryRoot(context)
                    ?? throw new NotFoundException(
                        $"{context.Environment.CurrentDirectory} is not inside a git repository or a site project.",
                        "Run it from the repository, or install for your user without --repo.");
                directory = SkillLocations.Repository(root);
            }
            else
            {
                directory = SkillLocations.User(context.Environment);
            }
            return Task.FromResult(new CommandResult(SkillInstaller.Install(BundledSkill.Load(), directory, context.Parse.GetValue(force)), Source: CommandResult.CliSource));
        });
        return command;
    }

    private static Command Print(GlobalOptions options)
    {
        var bundle = BundledSkill.Load();
        var file = new Argument<string>("file")
        {
            Description = $"Which file: {string.Join(" or ", bundle.Files.Keys.Order(StringComparer.Ordinal))}.",
            DefaultValueFactory = _ => SkillBundle.MainFile,
            HelpName = "file",
        };
        file.AcceptOnlyFromAmong([.. bundle.Files.Keys]);
        var command = new Command("print", """
            Write the bundled skill's SKILL.md (or reference.md) to stdout, as-is.
            Example: opticli skill print | head -20
            """);
        command.Arguments.Add(file);

        CommandRunner.SetHandler(command, options, (context, _) =>
            Task.FromResult(new CommandResult(null, Raw: bundle.Files[context.Parse.GetValue(file)!])));
        return command;
    }
}
