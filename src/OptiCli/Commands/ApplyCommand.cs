using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Seals;
using OptiCli.Core.Queries;
using OptiCli.Core.SourceScan;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class ApplyCommand
{
    public static Command Create(GlobalOptions options)
    {
        var file = new Argument<string>("plan.json") { Description = "The plan file, or - for stdin." };
        var allowOutside = new Option<bool>("--allow-outside")
        {
            Description = "Allow the plan to read files (uploads, @file values) outside the plan file's folder.",
        };
        var updateExisting = new Option<bool>("--update-existing")
        {
            Description = "Run a plan again: content that already has a step's GUID is updated (and moved back out of the recycle bin), and steps that are already done change nothing.",
        };
        var sealFor = new Option<string?>("--seal-for")
        {
            Description = "With --dry-run: check the plan against the local site and against production (read only: its content model and the versions of everything the plan touches), and seal it for that production database (name or id, role prod). Writes nothing but the seal, and prints the command the user runs in their own terminal (data.userCommand). Only create, change, move, translate and publish steps by GUID; no media.",
            HelpName = "name",
        };
        var seal = new Option<string?>("--seal")
        {
            Description = "Run a sealed plan against production (with --db <name>): only the user, in their own terminal. Shows what the plan does, asks for the database's name and the write login (never saved), and refuses if the plan, the build or production changed since the seal, or after 24 hours. Takes its options from the seal. An agent never runs it.",
            HelpName = "id",
        };
        var versionPerStep = new Option<bool>("--version-per-step")
        {
            Description = "Save each set, area and composition operation as a version of its own, as the single commands do, instead of into the draft the run already saved for that content and language.",
        };
        var write = new WriteOptions();
        write.Publish.Description = "Publish every set, create, area, composition, block, upload and translate operation (as if each had \"publish\": true).";
        var command = new Command("apply", $$$"""
            Run several writes from a JSON plan, validating every one before anything is saved. Needs `opticli serve`.
            Each operation gets a dry run first; nothing is written unless all pass. Operations on content the plan creates
            are dry-run against a stand-in: the content as it will be after that operation, under its nearest existing
            ancestor (status "simulated"; values that refer to other planned content are checked when the plan runs, and
            their ContentArea placements against [AllowedTypes] in the code). So is a publish after earlier set, area or
            translate operations on the same content and language (those changes, published), a set or area after one with
            "from" (on that version, with the changes since), and a set on a language branch an earlier translate creates.
            The rest get their names checked ("deferred"). Then they run in order; on a failure
            opticli stops and reports what was saved (refs and versions), what failed, and how to undo each saved operation.
            A set, area or composition operation on content and a language this run already saved a draft of saves into
            that draft, as the edit UI does ("savedInto" names it and the operation that made it), so a plan leaves one
            draft per content and language; after a publish the next change starts a new one. --version-per-step saves a
            version per operation instead. "from", "baseVersion", "force" and a ref naming a version always make a new version.
            Plan: {"operations": [ {"op": "...", ...}, ... ]} ("source", which `opticli export` writes, is accepted and not
            read). A create, block or upload operation may have an "id"; later
            operations refer to what it created as "$id", in ref fields and as a whole string value in "properties".
            A string value "@path" in "properties" is that file's text (e.g. "MainBody": "@texts/article.html"; "@@" for a
            literal @). These files and an upload's "file" are relative to the plan file (the working directory for stdin)
            and must stay inside its folder unless --allow-outside.
            To run a plan again (after a database refresh, or to repair content editors changed), give it fixed GUIDs:
            "guidNamespace": "<a GUID>" next to "operations" derives each create/block/upload's GUID from its "id" (a step's
            "guid" overrides it), so its content has the same GUID and permanent links (~/link/<guid-without-dashes>.aspx) in
            every database. Without --update-existing a GUID that exists is a conflict; with it, that content is updated
            (moved back out of the recycle bin first if needed), and area adds, translations, publishes, deletes and restores that are
            already done are skipped. Content the plan no longer has is left alone. Each step reports its "guid".
            Operations and fields (* required; the same as the matching command):
            {{{string.Join(Environment.NewLine, WritePlan.Fields.Select(f => $"  {f.Key}: {string.Join(", ", f.Value)}"))}}}
            area: action is add (item = block ref, at, display), remove (index or item) or move (index or item, to).
            set, area: "from": "published" (or a version, 456 or "123_456") bases the change on that version instead of the
            latest, leaving newer drafts out (as --from); later operations on that content and language build on its result.
            CMS 13 (Visual Builder): "composition" in a create's or set's "properties" is the whole composition, as get shows
            it ("@path" reads it from a JSON file); create takes "blueprint" instead of "type"; composition: action is add
            (nodeType, in, at, value: the node as get's composition shows one), remove (node), move (node, in, at) or set
            (node, value: name, displayTemplate, displaySettings, properties); a node is its key, or its name if unique.
            "variation" on set and composition changes that content variation.
            Example: opticli apply plan.json --dry-run
              {"operations": [
                {"op": "create", "id": "page", "parent": "123", "type": "ArticlePage", "name": "News", "properties": {"Heading": "Hi"}},
                {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "for": "$page"},
                {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "$teaser"}]}
            """);
        command.Arguments.Add(file);
        command.Options.Add(allowOutside);
        command.Options.Add(updateExisting);
        command.Options.Add(sealFor);
        command.Options.Add(seal);
        command.Options.Add(versionPerStep);
        write.AddCommon(command);
        // A sealed run (--seal) refuses everything but production itself, and is its own narrow way past the refusal: the
        // write commands' check before the command runs would stop it first. Every other apply is checked as a write.
        WriteCommands.Mark(command, parse => parse.GetValue(seal) is null);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var path = parse.GetValue(file)!;
            if (parse.GetValue(seal) is { } sealId)
            {
                var given = new (bool Given, string Name)[]
                {
                    (parse.GetValue(sealFor) is not null, "--seal-for"), (parse.GetValue(write.DryRun), "--dry-run"), (parse.GetValue(write.Publish), "--publish"),
                    (parse.GetValue(updateExisting), "--update-existing"), (parse.GetValue(allowOutside), "--allow-outside"), (parse.GetValue(write.RequestApproval), "--request-approval"),
                    (parse.GetValue(write.AcceptDrift) is not null, "--accept-drift"), (parse.GetValue(options.Connection) is not null, "--connection"), (parse.GetValue(options.Profile) is not null, "--profile"),
                    (parse.GetValue(versionPerStep), "--version-per-step"),
                }.Where(o => o.Given).Select(o => o.Name).ToList();
                if (given.Count > 0)
                {
                    throw new UsageException($"--seal takes no {string.Join(", ", given)}: the run's options are the ones the plan was sealed with, and production is named with --db.",
                        "Run exactly the command the seal printed: opticli apply <plan> --db <name> --seal <id>.");
                }
                if (parse.GetValue(options.Database) is not { } production || path == "-")
                {
                    throw new UsageException("--seal needs the plan file and --db naming the production database it was sealed for.", "Run exactly the command the seal printed: opticli apply <plan> --db <name> --seal <id>.");
                }
                return await SealedApply.RunAsync(context, path, sealId, production, cancellationToken);
            }
            if (parse.GetValue(sealFor) is { } sealTarget)
            {
                if (parse.GetValue(versionPerStep))
                {
                    throw new UsageException("--version-per-step doesn't apply to a sealed plan: its run saves the changes to each content and language into one draft.");
                }
                return await SealAsync(context, path, sealTarget, parse.GetValue(write.DryRun), parse.GetValue(updateExisting), parse.GetValue(write.Publish),
                    parse.GetValue(write.RequestApproval), parse.GetValue(allowOutside), parse.GetValue(write.AcceptDrift), cancellationToken);
            }
            var plan = WritePlan.Parse(await ReadPlanAsync(path, context.Environment.CurrentDirectory, cancellationToken));
            var planDirectory = path == "-" ? context.Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(path, context.Environment.CurrentDirectory))!;
            await using var session = await context.OpenContentAsync(cancellationToken);
            // A plan confirms other people's drafts per step ("includeDraft"), never by a prompt halfway through.
            var writes = context.Writes(session, updateExisting: parse.GetValue(updateExisting), ask: false);
            var project = context.TryGetProject(out _);
            var allowedTypes = project is null ? null : new AllowedTypesCheck(session.Model, () => CSharpSourceIndex.Build(project.SourceRoot));
            var runner = new PlanRunner(session, writes, planDirectory, parse.GetValue(allowOutside), allowedTypes, parse.GetValue(write.RequestApproval))
            {
                VersionPerStep = parse.GetValue(versionPerStep),
            };
            var run = await runner.RunAsync(plan, parse.GetValue(write.DryRun), parse.GetValue(write.Publish), cancellationToken);
            return new CommandResult(run, Warnings: run.DryRun ? [Coverage(run, runner.VersionPerStep)] : null, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    /// <summary><c>apply &lt;plan&gt; --dry-run --seal-for &lt;prod&gt;</c>: checks the plan and seals it for the user to run.</summary>
    private static async Task<CommandResult> SealAsync(CliContext context, string path, string production, bool dryRun, bool updateExisting, bool publish, bool requestApproval,
        bool allowOutside, string? acceptDrift, CancellationToken cancellationToken)
    {
        if (!dryRun)
        {
            throw new UsageException("--seal-for seals a dry run: add --dry-run. Nothing is written to production by sealing; the user runs the sealed plan.");
        }
        if (path == "-" || allowOutside || acceptDrift is not null)
        {
            throw new UsageException("A sealed plan is a plan file whose files stay in its folder, run against a production database whose content model is the build's: no stdin, --allow-outside or --accept-drift.");
        }
        var (candidate, connection) = Sealing.Production(context, production, "--seal-for");
        Sealing.RequireLocal(context, candidate, "Sealing");
        var plan = await PlanSnapshot.ReadAsync(Path.GetFullPath(path, context.Environment.CurrentDirectory), cancellationToken);
        // What can be checked without a database first; reference values once the model is known.
        SealedPlan.Require(plan.Plan, model: null);
        await using var session = await context.OpenContentAsync(cancellationToken);
        await using var productionSession = await ContentSession.OpenAsync(await CmsDatabase.OpenAsync(connection, cancellationToken), cancellationToken);
        var warnings = new List<string>();
        var result = await Sealing.SealAsync(context, plan, production, new SealOptions(updateExisting, publish, requestApproval), "apply", session, candidate, productionSession, warnings, cancellationToken);
        return new CommandResult(result, Text: Sealing.Text(result), Warnings: warnings.Count > 0 ? warnings : null, Source: WriteExecutor.AgentSource);
    }

    /// <summary>How thoroughly the dry run could check the plan.</summary>
    /// <param name="versionPerStep">The real run saves a version per operation (<c>--version-per-step</c>).</param>
    internal static string Coverage(PlanRun run, bool versionPerStep = false)
    {
        int Count(string status) => run.Operations.Count(o => o.Status == status);
        var deferred = Count(PlanStepStatus.Deferred);
        var coverage = $"Dry run of {run.Operations.Count} operation(s): {Count(PlanStepStatus.Valid)} checked by the site, "
            + $"{Count(PlanStepStatus.Simulated)} checked as their content will be after earlier operations, or against stand-ins for content the plan creates (their warnings say what wasn't), "
            + $"{deferred} only name-checked{(deferred > 0 ? " (the site validates them when the plan runs)" : "")}.";
        // A change after a step that may have saved a draft (of the same content, or not: kept simple).
        var draftFirst = run.Operations.Select(o => o.Op).ToList().FindIndex(op => op is "set" or "area" or "composition" or "create" or "block" or "upload" or "translate");
        var savesInto = draftFirst >= 0 && run.Operations.Skip(draftFirst + 1).Any(o => o.Op is "set" or "area" or "composition");
        return savesInto && !versionPerStep
            ? $"{coverage} When it runs, a set, area or composition operation on content and a language that an earlier operation saved a draft of saves into that draft (one draft per content and language; --version-per-step: a version each)."
            : coverage;
    }

    private static async Task<string> ReadPlanAsync(string path, string currentDirectory, CancellationToken cancellationToken)
    {
        if (path == "-")
        {
            return await Console.In.ReadToEndAsync(cancellationToken);
        }
        var full = Path.GetFullPath(path, currentDirectory);
        return File.Exists(full)
            ? await File.ReadAllTextAsync(full, cancellationToken)
            : throw new NotFoundException($"Plan file {full} does not exist.");
    }
}
