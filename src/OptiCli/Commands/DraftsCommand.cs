using System.CommandLine;
using System.Globalization;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class DraftsCommand
{
    public static Command Create(GlobalOptions options)
    {
        var since = new Option<string?>("--since") { Description = "Only drafts saved on or after this UTC date (yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss).", HelpName = "date" };
        var by = new Option<string?>("--by") { Description = "Only drafts saved by a user whose name contains this.", HelpName = "user" };
        var kind = new Option<string?>("--kind") { Description = "Only content of this kind: page, block, media, folder or other; on CMS 13 also experience, section or element (page includes experiences, block sections and elements).", HelpName = "kind" };
        kind.AcceptOnlyFromAmong(Enum.GetNames<ContentKind>().Select(n => n.ToLowerInvariant()).ToArray());
        var type = new Option<string?>("--type") { Description = "Only content of this type (name or GUID).", HelpName = "type" };
        var content = new ContentOptions();
        content.Lang.Description = "Only drafts in this language (code). Default: all languages.";
        var list = new ListOptions(options);
        var blueprints = BlueprintsOption.Create();
        var command = new Command("drafts", """
            List unpublished changes across the site, newest first.
            One row per content item and language whose branch was never published or has versions newer than the published
            one: status and version are the newest draft's (checkedOut = being edited, checkedIn = ready to publish, also
            awaitingApproval, delayedPublish, rejected), saved (UTC) and changedBy say when and by whom, and drafts counts
            the unpublished versions newer than the published one (thousands usually mean an import or integration job).
            CMS 13: a content variation with an unpublished version newer than its own published one gets a row of its own,
            with variation (`opticli get <version>` shows it); Visual Builder blueprints are left out unless --blueprints.
            Example: opticli drafts --since 2024-01-01 --kind page --by editor
            """);
        command.Options.Add(since);
        command.Options.Add(by);
        command.Options.Add(kind);
        command.Options.Add(type);
        content.AddTo(command, withRef: false, withSite: false);
        list.AddTo(command);
        command.Options.Add(blueprints);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            DateTime? from = null;
            if (context.Parse.GetValue(since) is { } text)
            {
                // Saved dates are UTC in the database: read the input as UTC, and convert an explicit offset to it.
                from = DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                    ? date
                    : throw new UsageException($"Invalid --since '{text}'.", "Use yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss (UTC; a Z or +01:00 suffix is honoured).");
            }
            await using var session = await context.OpenContentAsync(cancellationToken);
            var (offset, limit) = list.Window(context.Parse);
            var types = session.Model.Types.AsEnumerable();
            if (context.Parse.GetValue(type) is { } typeName)
            {
                types = [session.Model.RequireType(typeName)];
            }
            if (context.Parse.GetValue(kind) is { } wanted)
            {
                var parsed = Enum.Parse<ContentKind>(wanted, ignoreCase: true);
                types = types.Where(t => t.Kind.Matches(parsed));
            }
            var typeIds = context.Parse.GetValue(type) is null && context.Parse.GetValue(kind) is null ? null : types.Select(t => t.Id).ToList();
            var drafts = await new DraftReader(session, context.Parse.GetValue(blueprints)).ListAsync(
                from, context.Parse.GetValue(by), session.Language(context.Parse.GetValue(content.Lang)), typeIds, offset, limit, cancellationToken);
            return CommandResult.From(Paging.FromWindow(drafts, offset, limit));
        });
        return command;
    }
}
