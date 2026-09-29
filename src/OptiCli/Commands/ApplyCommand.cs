using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class ApplyCommand
{
    public static Command Create(GlobalOptions options)
    {
        var file = new Argument<string>("plan.json") { Description = "The plan file, or - for stdin." };
        var write = new WriteOptions();
        write.Publish.Description = "Publish every set, create, area, block and translate operation (as if each had \"publish\": true).";
        var command = new Command("apply", $$$"""
            Run several writes from a JSON plan, validating every one before anything is saved. Needs `opticli serve`.
            Each operation gets a dry run first (operations on content created earlier in the plan get their names checked
            and are validated when they run); nothing is written unless all pass. Then they run in order; on a failure
            opticli stops and reports what was saved (refs and versions), what failed, and how to undo each saved operation.
            Plan: {"operations": [ {"op": "...", ...}, ... ]}. A create or block operation may have an "id"; later operations
            refer to what it created as "$id", in ref fields and as a whole string value in "properties".
            Operations and fields (* required; the same as the matching command):
            {{{string.Join(Environment.NewLine, WritePlan.Fields.Select(f => $"  {f.Key}: {string.Join(", ", f.Value)}"))}}}
            area: action is add (item = block ref, at, display), remove (index or item) or move (index or item, to).
            Example: opticli apply plan.json --dry-run
              {"operations": [
                {"op": "create", "id": "page", "parent": "123", "type": "ArticlePage", "name": "News", "properties": {"Heading": "Hi"}},
                {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "for": "$page"},
                {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "$teaser"}]}
            """);
        command.Arguments.Add(file);
        write.AddCommon(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var plan = WritePlan.Parse(await ReadPlanAsync(parse.GetValue(file)!, context.Environment.CurrentDirectory, cancellationToken));
            await using var session = await context.OpenContentAsync(cancellationToken);
            var run = await new PlanRunner(session, context.Writes(session)).RunAsync(plan, parse.GetValue(write.DryRun), parse.GetValue(write.Publish), cancellationToken);
            return new CommandResult(run, Source: WriteExecutor.AgentSource);
        });
        return command;
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
