using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
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
        var write = new WriteOptions();
        write.Publish.Description = "Publish every set, create, area, block, upload and translate operation (as if each had \"publish\": true).";
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
            Plan: {"operations": [ {"op": "...", ...}, ... ]}. A create, block or upload operation may have an "id"; later
            operations refer to what it created as "$id", in ref fields and as a whole string value in "properties".
            A string value "@path" in "properties" is that file's text (e.g. "MainBody": "@texts/article.html"; "@@" for a
            literal @). These files and an upload's "file" are relative to the plan file (the working directory for stdin)
            and must stay inside its folder unless --allow-outside.
            To run a plan again (after a database refresh, or to repair content editors changed), give it fixed GUIDs:
            "guidNamespace": "<a GUID>" next to "operations" derives each create/block/upload's GUID from its "id" (a step's
            "guid" overrides it), so its content has the same GUID and permanent links (~/link/<guid-without-dashes>.aspx) in
            every database. Without --update-existing a GUID that exists is a conflict; with it, that content is updated
            (moved back out of the recycle bin first if needed), and area adds, translations, publishes and deletes that are
            already done are skipped. Content the plan no longer has is left alone. Each step reports its "guid".
            Operations and fields (* required; the same as the matching command):
            {{{string.Join(Environment.NewLine, WritePlan.Fields.Select(f => $"  {f.Key}: {string.Join(", ", f.Value)}"))}}}
            area: action is add (item = block ref, at, display), remove (index or item) or move (index or item, to).
            set, area: "from": "published" (or a version, 456 or "123_456") bases the change on that version instead of the
            latest, leaving newer drafts out (as --from); later operations on that content and language build on its result.
            Example: opticli apply plan.json --dry-run
              {"operations": [
                {"op": "create", "id": "page", "parent": "123", "type": "ArticlePage", "name": "News", "properties": {"Heading": "Hi"}},
                {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "for": "$page"},
                {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "$teaser"}]}
            """);
        command.Arguments.Add(file);
        command.Options.Add(allowOutside);
        command.Options.Add(updateExisting);
        write.AddCommon(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var path = parse.GetValue(file)!;
            var plan = WritePlan.Parse(await ReadPlanAsync(path, context.Environment.CurrentDirectory, cancellationToken));
            var planDirectory = path == "-" ? context.Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(path, context.Environment.CurrentDirectory))!;
            await using var session = await context.OpenContentAsync(cancellationToken);
            // A plan confirms other people's drafts per step ("includeDraft"), never by a prompt halfway through.
            var writes = context.Writes(session, updateExisting: parse.GetValue(updateExisting), ask: false);
            var project = context.TryGetProject(out _);
            var allowedTypes = project is null ? null : new AllowedTypesCheck(session.Model, () => CSharpSourceIndex.Build(project.SourceRoot));
            var run = await new PlanRunner(session, writes, planDirectory, parse.GetValue(allowOutside), allowedTypes, parse.GetValue(write.RequestApproval)).RunAsync(plan, parse.GetValue(write.DryRun), parse.GetValue(write.Publish), cancellationToken);
            return new CommandResult(run, Warnings: run.DryRun ? [Coverage(run)] : null, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    /// <summary>How thoroughly the dry run could check the plan.</summary>
    private static string Coverage(PlanRun run)
    {
        int Count(string status) => run.Operations.Count(o => o.Status == status);
        var deferred = Count(PlanStepStatus.Deferred);
        return $"Dry run of {run.Operations.Count} operation(s): {Count(PlanStepStatus.Valid)} checked by the site, "
            + $"{Count(PlanStepStatus.Simulated)} checked as their content will be after earlier operations, or against stand-ins for content the plan creates (their warnings say what wasn't), "
            + $"{deferred} only name-checked{(deferred > 0 ? " (the site validates them when the plan runs)" : "")}.";
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
