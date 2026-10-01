using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Commands;

internal static class AccessCommand
{
    /// <param name="Approval">The content approval sequence publishing goes through, when one applies.</param>
    private sealed record AccessView(string Ref, Guid Guid, string? Type, string? Name, bool Inherited, string? From, IReadOnlyList<AccessEntry> Entries, ApprovalSequence? Approval);

    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var grant = new Option<string[]>("--grant")
        {
            Description = $"Give a role exactly these levels: Role=Levels, levels {AccessLevels.Syntax}. Repeatable.",
            HelpName = "role=levels",
            AllowMultipleArgumentsPerToken = false,
        };
        var user = new Option<string[]>("--user")
        {
            Description = "Give a user exactly these levels: Name=Levels. Repeatable.",
            HelpName = "name=levels",
            AllowMultipleArgumentsPerToken = false,
        };
        var revoke = new Option<string[]>("--revoke")
        {
            Description = "Remove the entry for this role or user (an item has one per name). Repeatable; applied before the grants.",
            HelpName = "name",
            AllowMultipleArgumentsPerToken = false,
        };
        var breakInheritance = new Option<bool>("--break-inheritance")
        {
            Description = "Give an item that inherits its own access rights, starting from the inherited entries (like unticking \"Inherit settings from parent item\").",
        };
        var inherit = new Option<bool>("--inherit") { Description = "Drop the item's own entries so it inherits from its parent again." };
        var allowUnknown = new Option<bool>("--allow-unknown-role")
        {
            Description = "Accept a role the site doesn't know yet, e.g. one an identity provider creates on first sign-in.",
        };
        var write = new WriteOptions();
        var command = new Command("access", """
            Show or change who may read and edit a content item (its access rights, or ACL).
            Without options it shows the effective entries, read from the database: whether they are inherited, and from where.
            With options it changes them through the site (needs `opticli serve`), for this item only: children that inherit
            follow automatically, nothing is applied to descendants. An inherited ACL can only be changed with
            --break-inheritance. The root, the recycle bin, start pages and asset roots are refused (exit 3), and so is any
            change that leaves no role with Administer. Access rights aren't versioned: the output shows before and after, and
            the undo is the inverse command.
            Example: opticli access /en/members/ --break-inheritance --revoke Everyone --grant Authenticated=Read --dry-run
            """);
        content.AddTo(command, withLang: false);
        command.Options.Add(grant);
        command.Options.Add(user);
        command.Options.Add(revoke);
        command.Options.Add(breakInheritance);
        command.Options.Add(inherit);
        command.Options.Add(allowUnknown);
        write.AddCommon(command, publish: false);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new AccessOperation(
                parse.GetValue(content.Ref)!,
                Assignments(parse.GetValue(grant), "--grant"),
                Assignments(parse.GetValue(user), "--user"),
                parse.GetValue(revoke) is { Length: > 0 } names ? names : null,
                parse.GetValue(breakInheritance),
                parse.GetValue(inherit),
                parse.GetValue(allowUnknown));
            var changes = operation.Grant is not null || operation.GrantUsers is not null || operation.Revoke is not null || operation.BreakInheritance || operation.Inherit;

            await using var session = await context.OpenContentAsync(cancellationToken);
            if (changes)
            {
                return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
            }
            if (parse.GetValue(write.DryRun) || operation.AllowUnknownRole)
            {
                throw new UsageException("--dry-run and --allow-unknown-role only apply to a change.", "Give --grant, --user, --revoke, --break-inheritance or --inherit, or leave them out to show the access rights.");
            }

            var located = await content.LocateAsync(context, session, cancellationToken);
            var header = await session.HeaderAsync(located.Id, cancellationToken);
            var identity = session.Identities.Describe(header, null);
            var access = await AccessReader.ReadAsync(session.Db, header, cancellationToken);
            var approval = await ApprovalReader.ResolveAsync(session.Db, session.Model, header, cancellationToken);
            return new CommandResult(new AccessView(identity.Ref!, header.Guid, identity.Type, identity.Name, access.Inherited, access.From, access.Entries, approval));
        });
        return command;
    }

    /// <summary><c>Name=Levels</c> pairs; the last <c>=</c> separates them, since levels never contain one.</summary>
    private static IReadOnlyDictionary<string, string>? Assignments(string[]? values, string option)
    {
        if (values is not { Length: > 0 })
        {
            return null;
        }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var equals = value.LastIndexOf('=');
            if (equals <= 0 || equals == value.Length - 1)
            {
                throw new UsageException($"{option} '{value}' is not Name=Levels.", $"For example {option} Authenticated=Read or {option} \"Web Editors=Read,Edit\"; levels are {AccessLevels.Syntax}.");
            }
            var name = value[..equals].Trim();
            if (!result.TryAdd(name, value[(equals + 1)..].Trim()))
            {
                throw new UsageException($"{option} names '{name}' more than once.");
            }
        }
        return result;
    }
}
