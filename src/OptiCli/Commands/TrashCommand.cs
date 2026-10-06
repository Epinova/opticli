using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Jobs;
using OptiCli.Core.Output;
using OptiCli.Core.Queries;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

/// <summary>Lists what is in the recycle bin, with where <c>restore</c> puts each item back (a database read).</summary>
internal static class TrashCommand
{
    public static Command Create(GlobalOptions options)
    {
        var since = new Option<string?>("--since") { Description = $"Only content deleted since: {JobTimes.SinceSyntax}.", HelpName = "date|age" };
        var by = new Option<string?>("--by") { Description = "Only content deleted by a user whose name contains this.", HelpName = "user" };
        var type = new Option<string?>("--type") { Description = "Only content of this type (name or GUID).", HelpName = "type" };
        var list = new ListOptions(options);
        var command = new Command("trash", """
            List the recycle bin, newest first: what was deleted (each item directly in the bin; what was below it is counted
            in descendants and comes back with it), who deleted it and when (deletedBy, deleted, UTC), and originalParent:
            where `opticli restore <ref>` puts it back, the parent the CMS stored when it was deleted (ref, name, path, url).
            originalParent is null when the CMS has no record of it (restore --to then), and has deleted: true when that
            parent is in the recycle bin too (restore it first). Read from the database; nothing needs to run. While
            `opticli serve` runs, the stored parents come from the site, as restore reads them (meta.source: agent);
            otherwise from the database, with a warning saying why.
            Example: opticli trash --since 7d --by opticli
            """);
        command.Options.Add(since);
        command.Options.Add(by);
        command.Options.Add(type);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var from = parse.GetValue(since) is { } text ? JobTimes.ParseSince(text, DateTime.UtcNow) : (DateTime?)null;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var typeIds = parse.GetValue(type) is { } typeName ? [session.Model.RequireType(typeName).Id] : (IReadOnlyCollection<int>?)null;
            var (offset, limit) = list.Window(parse);
            // The site, when serve runs, answers as restore will; otherwise the database's copy of the CMS's store.
            var reader = new TrashReader(session, context.ConnectAgentAsync);
            var items = await reader.ListAsync(from, parse.GetValue(by), typeIds, offset, limit, cancellationToken);
            var page = Paging.FromWindow(items, offset, limit);
            var warnings = new List<string>();
            if (reader.NotFromSite is { } why && page.Items.Count > 0)
            {
                warnings.Add($"originalParent was read from the database ({why}); restore asks the site, which can differ when the CMS maps its store differently.");
            }
            if (page.Items.Where(i => i.OriginalParent is null).Select(i => i.Ref).ToList() is { Count: > 0 } unknown)
            {
                warnings.Add($"The CMS has no record of where {string.Join(", ", unknown)} {(unknown.Count == 1 ? "was" : "were")} before deletion (originalParent: null): `opticli restore <ref> --to <parent>` brings {(unknown.Count == 1 ? "it" : "one")} back.");
            }
            if (page.Items.Where(i => i.OriginalParent is { Deleted: true } or { Missing: true }).Select(i => i.Ref).ToList() is { Count: > 0 } orphaned)
            {
                warnings.Add($"The original parent of {string.Join(", ", orphaned)} is in the recycle bin too, or gone: restore that parent first, or use --to.");
            }
            // The items are the database's; their parents the site's when it answered.
            return new CommandResult(page.Items, page.Next, Warnings: warnings.Count > 0 ? warnings : null,
                Source: reader.ParentsFrom == "site" ? WriteExecutor.AgentSource : CommandResult.DbSource);
        });
        return command;
    }
}
