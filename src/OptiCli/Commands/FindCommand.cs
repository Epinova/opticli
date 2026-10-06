using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Output;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class FindCommand
{
    /// <summary>An item of <c>--status scheduled|expired</c>: its identity, with when it goes live or went offline (UTC).</summary>
    private sealed record TimedItem(string? Ref, Guid Guid, string? Type, string? Name, string? Language, string? Status, string? Url, DateTime? PublishAt, DateTime? ExpiredAt)
    {
        public static TimedItem From(Core.Content.ContentIdentity identity, DateTime? publishAt, DateTime? expiredAt) =>
            new(identity.Ref, identity.Guid, identity.Type, identity.Name, identity.Language, identity.Status, identity.Url, publishAt, expiredAt);
    }

    public static Command Create(GlobalOptions options)
    {
        var type = new Option<string>("--type") { Description = "Content type name or GUID.", Required = true, HelpName = "type" };
        var where = new Option<string[]>("--where")
        {
            Description = "Filter on a property, repeatable: Prop=value (exact) or Prop~value (contains). Block.Prop reaches into a local block; "
                + "Name filters on the item name; for ContentArea/references the value is a ref (items containing it).",
            HelpName = "Prop=value",
            AllowMultipleArgumentsPerToken = false,
        };
        var under = new Option<string?>("--under") { Description = "Only descendants of this ref.", HelpName = "ref" };
        var status = new Option<string>("--status")
        {
            Description = "published, draft (never published or has newer unpublished changes), scheduled (a version waits to be published at a set time: publishAt), expired (published, but its stop-publish date has passed: expiredAt) or any.",
            DefaultValueFactory = _ => "any",
            HelpName = "published|draft|scheduled|expired|any",
        };
        status.AcceptOnlyFromAmong("published", "draft", "scheduled", "expired", "any");
        var content = new ContentOptions();
        content.Lang.Description = "Match and show items in this branch (code); items without it are skipped. Default: each item's master language.";
        var list = new ListOptions(options);

        var command = new Command("find", """
            List content items of one type, optionally filtered on property values, location and status.
            Filters run in SQL on each item's primary (published) values. Without --lang every item is matched and shown in its
            master language; with --lang only items that have that branch. A ContentArea or reference filter takes a ref:
            --where MainArea=456 finds the items whose MainArea contains content 456. --status scheduled lists items with a version
            the CMS's "Publish delayed content versions" job publishes later (publishAt, UTC); --status expired items whose
            published version has stopped publishing (expiredAt, UTC), which visitors no longer see.
            Example: opticli find --type ArticlePage --where Heading~news --under /en/ --status published
            Example: opticli find --type ArticlePage --status scheduled
            """);
        command.Options.Add(type);
        command.Options.Add(where);
        command.Options.Add(under);
        command.Options.Add(status);
        content.AddTo(command, withRef: false);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var contentType = session.Model.RequireType(context.Parse.GetValue(type)!);
            var clauses = (context.Parse.GetValue(where) ?? []).Select(WhereClause.Parse).ToList();
            var underRef = context.Parse.GetValue(under);
            int? underId = underRef is null ? null : (await session.LocateAsync(underRef, context.Parse.GetValue(content.Site), cancellationToken)).Id;
            var language = session.Language(context.Parse.GetValue(content.Lang));
            var (offset, limit) = list.Window(context.Parse);

            var found = await new FindQuery(session).RunAsync(
                contentType, clauses, underId, Enum.Parse<FindStatus>(context.Parse.GetValue(status)!, ignoreCase: true), language, offset, limit, cancellationToken);
            var page = Paging.FromWindow(found, offset, limit);
            await session.Identities.LoadAsync(page.Items.Select(f => f.Id), [], cancellationToken);
            var items = page.Items.Select(f => (object)(f.PublishAt is null && f.ExpiredAt is null
                ? session.Identities.Describe(session.Identities.Header(f.Id)!, language)
                : TimedItem.From(session.Identities.Describe(session.Identities.Header(f.Id)!, language), f.PublishAt, f.ExpiredAt))).ToList();
            return new CommandResult(items, page.Next);
        });
        return command;
    }
}
