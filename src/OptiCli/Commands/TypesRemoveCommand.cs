using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// <c>types remove</c>, <c>types remove-property</c> and <c>types prune</c>: content types and properties that removed code
/// left in the database, removed through the site (needs <c>serve</c>), which decides what is an orphan.
/// </summary>
internal static class TypesRemoveCommand
{
    private const string Rules = """
        Only orphans, as the running site judges them: a type whose class the site can't load (never one made in admin mode
        or one of the CMS's own), and a property not in its type's code (existsOnModel: false) on a type defined in code.
        CMS 13 records no class or version for a type made in admin mode nor for a code type a content import overwrote
        (originUnknown in `opticli types --orphaned`): those go only with --include-unknown-origin.
        A type stays while content of it exists, also in the recycle bin, or a property uses it as its block type (conflict,
        exit 5): opticli never deletes content. Content types aren't versioned: the output records what was removed, to make
        it again by hand. Refused against a shared database (exit 3).
        """;

    public static IEnumerable<Command> Create(GlobalOptions options) => [Remove(options), RemoveProperty(options), Prune(options)];

    private static Command Remove(GlobalOptions options)
    {
        var names = new Argument<string[]>("types") { Description = "Content types to remove, by name or GUID.", Arity = ArgumentArity.OneOrMore };
        var write = new WriteOptions();
        write.DryRun.Description = "Run every check through the site and show what would be removed, without removing it.";
        var command = new Command("remove", $"""
            Remove content types whose class is gone from the code, through the site (needs `opticli serve`), with
            IContentTypeRepository.Delete, as admin mode's Delete does. All named types are checked first: if one can't go,
            none is removed. A type whose page-type properties' values name it is refused too, since the CMS would clear them.
            {Rules}
            Example: opticli types remove OldNewsPage --dry-run
            """);
        var unknown = IncludeUnknownOrigin();
        command.Arguments.Add(names);
        command.Options.Add(unknown);
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, (context, cancellationToken) =>
            RunAsync(context, new OrphanRemovalRequest
            {
                Types = Names(context.Parse.GetValue(names) ?? []),
                IncludeUnknownOrigin = context.Parse.GetValue(unknown),
                DryRun = context.Parse.GetValue(write.DryRun),
            }, cancellationToken));
        return command;
    }

    private static Command RemoveProperty(GlobalOptions options)
    {
        var type = new Argument<string>("type") { Description = "The content type, by name or GUID." };
        var properties = new Argument<string[]>("properties") { Description = "Its properties to remove.", Arity = ArgumentArity.OneOrMore };
        var allowDestructive = AllowDestructive();
        var write = new WriteOptions();
        write.DryRun.Description = "Run every check through the site and show what would be removed, with how many values, without removing it.";
        var command = new Command("remove-property", $"""
            Remove properties that aren't in their type's code any more (existsOnModel: false in `opticli type`), through the
            site (needs `opticli serve`), with IPropertyDefinitionRepository.Delete, as admin mode's Delete does. The CMS deletes
            the property's values with it, in every version and language, values inside a block property and its category
            selections included: a property with values needs --allow-destructive (refused, exit 3, otherwise). A property
            added in admin mode to a type with a class looks the same as one removed from the code: check before removing it.
            All named properties are checked first: if one can't go, none is removed.
            {Rules}
            Example: opticli types remove-property ArticlePage OldIntro --dry-run
            """);
        var unknown = IncludeUnknownOrigin();
        command.Arguments.Add(type);
        command.Arguments.Add(properties);
        command.Options.Add(allowDestructive);
        command.Options.Add(unknown);
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, (context, cancellationToken) =>
            RunAsync(context, new OrphanRemovalRequest
            {
                Properties = OrphanRemover.Properties(context.Parse.GetValue(type)!, Names(context.Parse.GetValue(properties) ?? [])),
                AllowDestructive = context.Parse.GetValue(allowDestructive),
                IncludeUnknownOrigin = context.Parse.GetValue(unknown),
                DryRun = context.Parse.GetValue(write.DryRun),
            }, cancellationToken));
        return command;
    }

    private static Command Prune(GlobalOptions options)
    {
        var withProperties = new Option<bool>("--properties")
        {
            Description = "Also remove properties that aren't in their type's code (existsOnModel: false). Off by default: one added in admin mode to a type with a class looks the same.",
        };
        var allowDestructive = AllowDestructive();
        var write = new WriteOptions();
        write.DryRun.Description = "Show what would be removed and what stays (kept, with why), through the site, without removing anything.";
        var command = new Command("prune", $"""
            Remove every orphaned content type that can go, through the site (needs `opticli serve`); with --properties, every
            orphaned property too (its values with --allow-destructive). What can't go is listed under kept with the reason
            (content of the type, a property using it as its block type, stored values, ...), and the rest is removed: a block
            type whose only users are properties removed in the same run goes after them. Dry-run it first.
            {Rules}
            Example: opticli types prune --dry-run
            Example: opticli types prune --properties --dry-run
            """);
        var unknown = IncludeUnknownOrigin();
        command.Options.Add(withProperties);
        command.Options.Add(allowDestructive);
        command.Options.Add(unknown);
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, (context, cancellationToken) =>
            RunAsync(context, new OrphanRemovalRequest
            {
                Prune = true,
                PruneProperties = context.Parse.GetValue(withProperties) || (context.Parse.GetValue(allowDestructive)
                    ? throw new Core.Errors.UsageException("--allow-destructive is for properties' values, which prune only removes with --properties.", "opticli types prune --properties --allow-destructive --dry-run")
                    : false),
                AllowDestructive = context.Parse.GetValue(allowDestructive),
                IncludeUnknownOrigin = context.Parse.GetValue(unknown),
                DryRun = context.Parse.GetValue(write.DryRun),
            }, cancellationToken));
        return command;
    }

    /// <summary>
    /// An option this command doesn't have lands among the names (they take any number): say so rather than look for a type
    /// named <c>--allow-destructive</c>.
    /// </summary>
    private static string[] Names(string[] names) =>
        names.FirstOrDefault(n => n.StartsWith('-')) is { } option
            ? throw new Core.Errors.UsageException($"'{option}' isn't an option of this command.", "Run it with --help for its options.")
            : names;

    private static Option<bool> IncludeUnknownOrigin() => new(OrphanRemoval.IncludeUnknownOriginFlag)
    {
        Description = "CMS 13: also remove types of unknown origin (originUnknown in `opticli types --orphaned`): no class or version on record and no class in the build with their GUID, so made in admin mode or code types a content import overwrote. Check with the user that none was made in admin mode on purpose.",
    };

    private static Option<bool> AllowDestructive() => new("--allow-destructive")
    {
        Description = "Remove properties that have stored values; the values are deleted with them, in every version and language, for good. Without it such a property is refused (exit 3). Never implied by anything else: ask the user first.",
    };

    private static async Task<CommandResult> RunAsync(CliContext context, OrphanRemovalRequest request, CancellationToken cancellationToken)
    {
        OrphanRemover.RequireLocal(!context.UseConnection().IsLocal);
        var agent = await context.ConnectAgentAsync(cancellationToken);
        var (output, warnings) = await OrphanRemover.RunAsync(agent, request, cancellationToken);
        return new CommandResult(output, Text: OrphanRemover.Text(output), Warnings: warnings.Count > 0 ? warnings : null, Source: WriteExecutor.AgentSource);
    }
}
