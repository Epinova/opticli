using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class LanguagesCommand
{
    public static Command Create(GlobalOptions options)
    {
        var all = new Option<bool>("--all") { Description = "Include disabled language branches." };
        var list = new ListOptions(options);
        var command = new Command("languages", """
            List language branches: code, name, which sites use it as master language, and how many content items it has.
            Disabled branches are only listed with --all. `languages enable|disable` changes them, through the site.
            Example: opticli languages
            """);
        command.Options.Add(all);
        list.AddTo(command);
        command.Subcommands.Add(Change(options, enable: true));
        command.Subcommands.Add(Change(options, enable: false));

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var languages = await LanguageReader.ListAsync(db, cancellationToken);
            var shown = context.Parse.GetValue(all) ? languages : languages.Where(l => l.Enabled).ToList();
            return CommandResult.From(list.Apply(context.Parse, shown));
        });
        return command;
    }

    /// <summary><c>languages enable|disable</c>: through the site, as admin mode's Manage Website Languages does.</summary>
    private static Command Change(GlobalOptions options, bool enable)
    {
        var codes = new Argument<string[]>("codes") { Description = "Language codes, as cultures name them: no, nb, sv, en-GB.", Arity = ArgumentArity.OneOrMore };
        var write = new WriteOptions();
        var command = new Command(enable ? "enable" : "disable", enable
            ? """
              Enable language branches, through the site (needs `opticli serve`), so content can be created and translated in
              them. A fresh database has en and sv enabled and a few others disabled; a code it has no branch for gets one,
              which Everyone may edit, as admin mode's Add does. Already enabled is `unchanged`. Refused against a shared
              database (exit 3). In a plan: {"op": "language", "lang": "no"}, before the steps that use it.
              Example: opticli languages enable no --dry-run
              """
            : """
              Disable language branches, through the site (needs `opticli serve`). Content in them stays in the database, but
              the site neither shows nor edits that language; the output warns about it, and about a site whose start page has
              it as master language. Disabling every enabled language is refused (exit 3), and so is a shared database.
              In a plan: {"op": "language", "lang": "da", "enabled": false}.
              Example: opticli languages disable da --dry-run
              """);
        command.Arguments.Add(codes);
        command.Options.Add(write.DryRun);
        WriteCommands.Mark(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var operation = new LanguageOperation(parse.GetValue(codes) ?? [], enable);
            return WriteOptions.Result(await context.Writes(session).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
