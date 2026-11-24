using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// <c>commerce get|children|tree</c>: catalog content read through the running site. <c>get</c>, <c>children</c> and
/// <c>tree</c> read the CMS database, where catalog content isn't (Commerce keeps it in its own), and point here.
/// </summary>
internal static class CommerceReadCommands
{
    private const string Refs = "A catalog ref: code:<code>, catalog:<name>, catalog-root, a GUID, or id__CatalogContent.";

    public static Command Get(GlobalOptions options)
    {
        var reference = new Argument<string>("ref") { Description = Refs };
        var lang = new Option<string?>("--lang") { Description = "The language branch to show. Default: its master language.", HelpName = "code" };
        var command = new Command("get", """
            One catalog item as the site loads it (Commerce's catalog content provider): ref, guid, name, type, language,
            master language and languages, status, parent, publish dates, and every property with its type and value
            (the catalog model's and Commerce's own: Code, DisplayName, SeoUri, ...). Its published version, else its latest.
            Example: opticli commerce get code:P-1001
            """);
        command.Arguments.Add(reference);
        command.Options.Add(lang);
        CommandRunner.SetHandler<ContentItem>(command, options, async (context, cancellationToken) =>
        {
            var found = await ResolveAsync(context, context.Parse.GetValue(reference)!, cancellationToken);
            var item = await CommerceCommand.ReadAsync<ContentItem>(context, AgentRoutes.Read(found.Ref!, context.Parse.GetValue(lang)), cancellationToken);
            // The site agent knows CMS kinds only (catalog content is "other" to it); the Commerce agent knows which catalog kind.
            item = item with { Kind = found.Kind ?? item.Kind };
            return new CommandResult<ContentItem>(item, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    public static Command Children(GlobalOptions options)
    {
        var reference = new Argument<string>("ref") { Description = Refs, Arity = ArgumentArity.ZeroOrOne };
        var limit = new Option<int?>("--limit") { Description = $"At most this many children (default 200); more says whether there were more.", HelpName = "n" };
        var command = new Command("children", """
            The catalog content below a catalog item, in catalog order: the catalogs below the catalog root (the default),
            the nodes and entries below a catalog or node. Each with ref, guid, name, type, kind (catalog, node, product,
            variation, bundle, package), code, parent, languages and version. A product's variants are its relations
            (`opticli commerce relations`), not its children.
            Example: opticli commerce children code:women
            """);
        command.Arguments.Add(reference);
        command.Options.Add(limit);
        CommandRunner.SetHandler<CatalogChildren>(command, options, async (context, cancellationToken) =>
        {
            var route = ChildrenRoute(context.Parse.GetValue(reference) ?? CatalogRefSyntax.Root, context.Parse.GetValue(limit));
            return new CommandResult<CatalogChildren>(await CommerceCommand.ReadAsync<CatalogChildren>(context, route, cancellationToken), Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    public static Command Tree(GlobalOptions options)
    {
        var reference = new Argument<string>("ref") { Description = Refs, Arity = ArgumentArity.ZeroOrOne };
        var depth = new Option<int>("--depth") { Description = "How many levels below it (default 3).", DefaultValueFactory = _ => 3, HelpName = "n" };
        var command = new Command("tree", """
            The catalog content below a catalog item, depth first, each with its depth: by default the catalog root and three
            levels (catalogs, their nodes, theirs). At most 200 children per item and 5000 items.
            Example: opticli commerce tree catalog:Fashion --depth 2
            """);
        command.Arguments.Add(reference);
        command.Options.Add(depth);
        CommandRunner.SetHandler<IReadOnlyList<CatalogTreeItem>>(command, options, async (context, cancellationToken) =>
        {
            var levels = context.Parse.GetValue(depth);
            if (levels < 0)
            {
                throw new UsageException("--depth can't be negative.");
            }
            var items = new List<CatalogTreeItem>();
            var warnings = new List<string>();
            async Task WalkAsync(string at, int level)
            {
                var children = await CommerceCommand.ReadAsync<CatalogChildren>(context, ChildrenRoute(at, null), cancellationToken);
                if (level == 0)
                {
                    items.Add(Item(0, children.Parent));
                }
                if (children.More)
                {
                    warnings.Add($"{children.Parent.Ref} has more than {children.Children.Count} children; the tree shows the first {children.Children.Count}.");
                }
                foreach (var child in children.Children)
                {
                    if (items.Count >= 5000)
                    {
                        return;
                    }
                    items.Add(Item(level + 1, child));
                    if (level + 1 < levels && child.Kind is CatalogKinds.Root or CatalogKinds.Catalog or CatalogKinds.Node)
                    {
                        await WalkAsync(child.Ref!, level + 1);
                    }
                }
            }
            await WalkAsync(context.Parse.GetValue(reference) ?? CatalogRefSyntax.Root, 0);
            if (items.Count >= 5000)
            {
                warnings.Add("The tree stops at 5000 items; give a ref lower down, or a smaller --depth.");
            }
            return new CommandResult<IReadOnlyList<CatalogTreeItem>>(items, Warnings: warnings.Count > 0 ? warnings : null, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    private static CatalogTreeItem Item(int depth, CatalogRef item) => new(depth, item.Ref!, item.Guid, item.Name, item.Type, item.Kind, item.Code);

    private static string ChildrenRoute(string reference, int? limit) =>
        AgentRoutes.WithRef(AgentRoutes.CommerceChildren, reference) + (limit is { } n ? $"&limit={n.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "");

    /// <exception cref="NotFoundException">The ref names no catalog content.</exception>
    private static async Task<CatalogRef> ResolveAsync(CliContext context, string reference, CancellationToken cancellationToken)
    {
        var agent = await context.ConnectAgentAsync(cancellationToken);
        CatalogRefsResponse response;
        try
        {
            response = await agent.SendAsync<CatalogRefsResponse>(HttpMethod.Post, AgentRoutes.CommerceRefs, new CatalogRefsRequest([reference]), cancellationToken);
        }
        catch (NotFoundException ex) when (ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        {
            await CommerceCommand.ReadAsync<CommerceInfo>(context, AgentRoutes.Commerce, cancellationToken);
            throw;
        }
        return response.Refs is [{ Found: true } found]
            ? found
            : throw new NotFoundException($"No catalog content '{reference.Trim()}'.", $"A catalog ref is {CatalogRefSyntax.Description}; `opticli commerce children` lists the catalogs.");
    }
}
