using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class ProjectsCommand
{
    private const string ProjectDescription = "The project: its id, or its exact name (`opticli projects` lists them).";

    public static Command Create(GlobalOptions options)
    {
        // Hidden, so the subcommands' usage lines don't carry it (the description says how to use it).
        var project = new Argument<string?>("project") { Description = "A project's id or exact name: list its items instead.", Arity = ArgumentArity.ZeroOrOne, Hidden = true };
        var list = new ListOptions(options);
        var command = new Command("projects", """
            List projects (edit mode's Projects: versions of several items reviewed and published together): id, name,
            status, who created it and when, publishAt when scheduled, how many items it has and how many of them are ready
            (marked Ready to publish: what publishing the project puts live). `opticli projects <project>` (its id or exact
            name) lists its items instead: the content, the version in the project, its status (checkedIn is ready,
            checkedOut a draft) and language.
            Subcommands write: create, ready, remove, publish, reactivate, delete. Saves join a project with --in-project on
            set, create, area, composition, block create, upload and translate (--ready marks them ready), or "project" in
            an apply plan. Publishing a project puts only its ready items live; the CMS 13 Projects gadget's own Publish
            puts every unpublished item live instead.
            Example: opticli projects "Spring campaign"
            """);
        command.Arguments.Add(project);
        list.AddTo(command);
        command.Subcommands.Add(CreateProject(options));
        command.Subcommands.Add(Ready(options));
        command.Subcommands.Add(Remove(options));
        command.Subcommands.Add(Publish(options));
        command.Subcommands.Add(Reactivate(options));
        command.Subcommands.Add(Delete(options));

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var projects = await ProjectReader.ListAsync(session.Db, cancellationToken);
            if (context.Parse.GetValue(project) is not { } reference)
            {
                return CommandResult.From(list.Apply(context.Parse, projects));
            }
            var found = ProjectReader.Require(projects, reference);
            return CommandResult.From(list.Apply(context.Parse, await ProjectReader.ItemsAsync(session, found.Id, cancellationToken)));
        });
        return command;
    }

    private static Command CreateProject(GlobalOptions options)
    {
        var name = new Argument<string>("name") { Description = "The new project's name (at most 255 characters)." };
        var write = new WriteOptions();
        var command = new Command("create", """
            Create a project (active, empty). Needs `opticli serve`. Refused (exit 5) when a project that isn't published has
            the name already, since projects are named by their name too. Put drafts in it with --in-project on the write
            commands. Prints the project (its id) and editorUi: projectMode (editors pick it in the project bar) or gadget
            (CMS 13's default: editors see it in the Projects gadget).
            Example: opticli projects create "Spring campaign"
            """);
        command.Arguments.Add(name);
        write.AddCommon(command, publish: false);
        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            return Result(await Writes(context, session).CreateAsync(context.Parse.GetValue(name)!, context.Parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }

    private static Command Ready(GlobalOptions options)
    {
        var project = ProjectArgument();
        var items = new Argument<string[]>("refs") { Description = "The items: content (every item of it, in --lang if given) or versions (123_456).", Arity = ArgumentArity.ZeroOrMore };
        var all = new Option<bool>("--all") { Description = "Every item that is a draft; items that are ready, in review, published or scheduled are left as they are (skipped)." };
        var lang = new Option<string?>("--lang") { Description = "Only the items in this language.", HelpName = "code" };
        var comment = new Option<string?>("--comment") { Description = "With --request-approval: a comment for the reviewers (required where the sequence says so).", HelpName = "text" };
        var write = new WriteOptions();
        write.RequestApproval.Description = "Where an approval sequence applies, send the item for review instead (marking it ready would skip the reviewers, so that is refused, exit 3); once approved it counts as ready. Items without a sequence are marked ready as usual.";
        write.IncludeDraft.Description = "Also mark ready items whose version someone else saved. The CMS then records opticli as the one who saved it, so the project's publish no longer asks about their changes: without it such an item is a conflict (exit 5), with pendingDraft in details.project.";
        var command = new Command("ready", """
            Mark project items Ready to publish, as the edit UI's "Ready to Publish" does (the version is checked in, and keeps
            its id). Needs `opticli serve`. Give refs or --all. Every item is checked before any is changed: an item that
            can't be marked (in review, published, a validation error, someone else's draft without --include-draft) stops
            it, with each item's problem in details.project. Publishing the project puts only ready items live.
            Example: opticli projects ready 12 123 124
            Also:    opticli projects ready "Spring campaign" --all --request-approval --comment "Campaign texts"
            """);
        command.Arguments.Add(project);
        command.Arguments.Add(items);
        command.Options.Add(all);
        command.Options.Add(lang);
        write.AddCommon(command, publish: false);
        write.AddRequestApproval(command);
        command.Options.Add(comment);
        write.AddIncludeDraft(command);
        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var refs = parse.GetValue(items) ?? [];
            if ((refs.Length > 0) == parse.GetValue(all))
            {
                throw new UsageException(refs.Length > 0 ? "Give the items to mark ready, or --all, not both." : "Give the items to mark ready (refs), or --all for every draft in the project.",
                    "Example: opticli projects ready 12 123 124, or opticli projects ready 12 --all.");
            }
            if (parse.GetValue(comment) is not null && !parse.GetValue(write.RequestApproval))
            {
                throw new UsageException("--comment goes to the reviewers of a review request: give --request-approval too, or leave the comment out.");
            }
            await using var session = await context.OpenContentAsync(cancellationToken);
            return Result(await Writes(context, session).ReadyAsync(parse.GetValue(project)!, refs.Length > 0 ? refs : null, parse.GetValue(lang), parse.GetValue(all),
                parse.GetValue(write.RequestApproval), parse.GetValue(comment), parse.GetValue(write.IncludeDraft), parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }

    private static Command Remove(GlobalOptions options)
    {
        var project = ProjectArgument();
        var items = new Argument<string[]>("refs") { Description = "The items to take out: content (every item of it, in --lang if given) or versions (123_456).", Arity = ArgumentArity.OneOrMore };
        var lang = new Option<string?>("--lang") { Description = "Only the items in this language.", HelpName = "code" };
        var write = new WriteOptions();
        var command = new Command("remove", """
            Take items out of a project. Needs `opticli serve`. Their versions stay as they are (drafts stay drafts). Only
            from an active project: a scheduled one is reactivated first.
            Example: opticli projects remove 12 123
            """);
        command.Arguments.Add(project);
        command.Arguments.Add(items);
        command.Options.Add(lang);
        write.AddCommon(command, publish: false);
        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            await using var session = await context.OpenContentAsync(cancellationToken);
            return Result(await Writes(context, session).RemoveAsync(parse.GetValue(project)!, parse.GetValue(items) ?? [], parse.GetValue(lang), parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }

    private static Command Publish(GlobalOptions options)
    {
        var project = ProjectArgument();
        var at = new Option<string?>("--at") { Description = $"Schedule the publish instead: {PublishTimes.Syntax}. The ready items become delayedPublish and the project delayedPublished until then; `opticli projects reactivate` cancels it.", HelpName = "time" };
        var write = new WriteOptions();
        write.IncludeDraft.Description = "Also publish the unpublished changes someone else saved after the published version, which ready items carry. Not needed for the changes of whoever marked an item ready (readyBy): marking it ready was their consent. Without it such a publish asks on a terminal, and elsewhere fails with a conflict (exit 5) listing them per item in details.project.";
        var command = new Command("publish", """
            Publish a project: its items marked Ready to publish go live together (or at --at), through the CMS's own project
            publisher. Needs `opticli serve`. Items that aren't ready (drafts, in review) stay as they are and are listed in
            notReady. Only ready items, also where editors use the Projects gadget (CMS 13's default), whose own Publish
            puts every unpublished item live. Every ready item is checked first (validation, approval, other people's
            drafts, except those of whoever marked the item ready): one problem stops the publish, with each item's problem in
            details.project, and nothing goes live. On a terminal it shows what goes live, whose changes go with it and what
            stays behind, and asks. Nothing ready is a conflict (exit 5).
            Prints published (or scheduled and scheduledFor), notReady, alreadyPublished, failed (with siteError) and
            siteChanges: what the site's own code changed meanwhile.
            Example: opticli projects publish 12 --dry-run
            Also:    opticli projects publish "Spring campaign" --at 2026-12-01T07:00Z
            """);
        command.Arguments.Add(project);
        command.Options.Add(at);
        write.AddCommon(command, publish: false);
        write.AddIncludeDraft(command);
        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var when = parse.GetValue(at) is { } text ? PublishTimes.Parse(text, "--at", DateTimeOffset.UtcNow) : (DateTimeOffset?)null;
            await using var session = await context.OpenContentAsync(cancellationToken);
            return Result(await Writes(context, session).PublishAsync(parse.GetValue(project)!, when, parse.GetValue(write.IncludeDraft), parse.GetValue(write.DryRun),
                DatabasePrompt.CanAsk ? ConfirmPrompt.Ask : null, cancellationToken));
        });
        return command;
    }

    private static Command Reactivate(GlobalOptions options)
    {
        var project = ProjectArgument();
        var write = new WriteOptions();
        var command = new Command("reactivate", """
            Cancel a scheduled project's publish (projects publish --at). Needs `opticli serve`. The project is active again,
            and its scheduled items are drafts again (the same versions), not ready: mark them ready again before publishing.
            Example: opticli projects reactivate 12
            """);
        command.Arguments.Add(project);
        write.AddCommon(command, publish: false);
        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            return Result(await Writes(context, session).ReactivateAsync(context.Parse.GetValue(project)!, context.Parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }

    private static Command Delete(GlobalOptions options)
    {
        var project = ProjectArgument();
        var write = new WriteOptions();
        var command = new Command("delete", """
            Delete a project, as the edit UI's "Delete Project" does. Needs `opticli serve`. Its items' versions stay as they
            are (drafts stay drafts, nothing is published or deleted). Not while it is scheduled (reactivate it first) or
            being published. --dry-run checks it in the database.
            Example: opticli projects delete 12 --dry-run
            """);
        command.Arguments.Add(project);
        write.AddCommon(command, publish: false);
        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var dryRun = context.Parse.GetValue(write.DryRun);
            await using var session = await context.OpenContentAsync(cancellationToken);
            var result = await Writes(context, session).DeleteAsync(context.Parse.GetValue(project)!, dryRun, cancellationToken);
            return result is Protocol.ProjectResult deleted ? Result(deleted) : new CommandResult(result, Source: WriteExecutor.DbSource);
        });
        return command;
    }

    private static Argument<string> ProjectArgument() => new("project") { Description = ProjectDescription };

    private static ProjectWrites Writes(CliContext context, Core.Content.ContentSession session) => new(context.Writes(session));

    private static CommandResult Result(Protocol.ProjectResult result) =>
        new(result, Warnings: Warnings(result) is { Count: > 0 } warnings ? warnings : null, Source: WriteExecutor.AgentSource);

    /// <summary>The result's warnings as <c>meta.warnings</c>: the operation's own, each item's, and what needs a look.</summary>
    internal static IReadOnlyList<string> Warnings(Protocol.ProjectResult result)
    {
        var warnings = (result.Validation ?? []).Where(v => v.Severity != "error").Select(v => v.Message).ToList();
        var items = (result.Items ?? []).Concat(result.Published ?? []).Concat(result.Scheduled ?? []).Concat(result.Failed ?? []);
        warnings.AddRange(items.SelectMany(i => (i.Validation ?? []).Where(v => v.Severity != "error").Select(v => $"{i.Content.Ref}: {v.Message}")));
        // Whose changes went (or go) live with the project, also those its readyBy consented to by marking it ready.
        warnings.AddRange((result.Published ?? []).Concat(result.Scheduled ?? []).Where(i => i.PendingDraft is not null && i.Problem is null).Select(i =>
            $"{i.Content.Ref}: {(result.DryRun ? "would put" : result.Scheduled is null ? "put" : "will put")} live {i.PendingDraft!.Describe()}{(i.ReadyBy is { } by ? $"; {by} marked it ready" : "")}."));
        if (result.Failed is { Count: > 0 } failed)
        {
            warnings.Add($"{failed.Count} ready item(s) failed to publish ({string.Join(", ", failed.Select(f => f.Content.Ref))}): the project is {result.Project.Status}, and publishing it again tries them again{(result.SiteError is { } error ? $". The CMS said: {error}" : "")}.");
        }
        if (result.SiteChanges is { Count: > 0 } changes)
        {
            warnings.Add($"The site's own code changed {changes.Count} other content item(s) while the project was published: see siteChanges.");
        }
        if (result is { DryRun: true, Valid: false })
        {
            warnings.Add("Dry run: the real run would be refused; each item's problem is in its problem field.");
        }
        return warnings;
    }
}
