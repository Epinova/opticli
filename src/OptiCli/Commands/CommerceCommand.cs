using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// <c>opticli commerce</c>: Optimizely Commerce, through the site's Commerce agent (needs <c>opticli serve</c>). Catalog
/// content itself is written with <c>create</c> and <c>set</c>; these are for what isn't content: relations, prices,
/// warehouses, stock and markets, and reads of them.
/// </summary>
internal static class CommerceCommand
{
    private const string Refs = "A catalog ref: code:<code> (a node or entry), catalog:<name>, catalog-root, a GUID, or id__CatalogContent as opticli prints it.";

    public static Command Create(GlobalOptions options)
    {
        var command = new Command("commerce", """
            Optimizely Commerce, through the site (needs `opticli serve` on a site whose build has EPiServer.Commerce.Core,
            which gets opticli's Commerce agent). Alone: the site's Commerce version, the catalog root and the catalogs.
            Catalog content (catalogs, nodes, products, variants, bundles, packages) is content: make and change it with
            `opticli create` and `opticli set`, below catalog refs (code:<code>, catalog:<name>, catalog-root). The
            subcommands are for the rest: relations (a product's variants, a bundle's entries), prices, warehouses, stock
            and markets, each also a plan step. Writes report status: created, updated, unchanged, removed or absent.
            Example: opticli commerce
            """);
        command.Subcommands.Add(CommerceReadCommands.Get(options));
        command.Subcommands.Add(CommerceReadCommands.Children(options));
        command.Subcommands.Add(CommerceReadCommands.Tree(options));
        command.Subcommands.Add(Relation(options));
        command.Subcommands.Add(Relations(options));
        command.Subcommands.Add(Price(options));
        command.Subcommands.Add(Prices(options));
        command.Subcommands.Add(Warehouse(options));
        command.Subcommands.Add(Warehouses(options));
        command.Subcommands.Add(Inventory(options));
        command.Subcommands.Add(Stock(options));
        command.Subcommands.Add(Market(options));
        command.Subcommands.Add(Markets(options));

        CommandRunner.SetHandler<CommerceInfo>(command, options, async (context, cancellationToken) =>
            new CommandResult<CommerceInfo>(await ReadAsync<CommerceInfo>(context, AgentRoutes.Commerce, cancellationToken), Source: WriteExecutor.AgentSource));
        return command;
    }

    private static Command Relation(GlobalOptions options)
    {
        var command = new Command("relation", "Add or remove a relation between catalog content: a product's variant, a bundle's or package's entry, an entry's extra category.");
        command.Subcommands.Add(RelationChange(options, remove: false));
        command.Subcommands.Add(RelationChange(options, remove: true));
        return command;
    }

    /// <summary>
    /// <c>--allow-destructive</c> of a removal: on a shared database it removes for good (Commerce has no recycle bin) and
    /// is refused without it.
    /// </summary>
    private static Option<bool> AllowDestructive() => new("--allow-destructive")
    {
        Description = "On a shared database (shared mode), remove it for good: Commerce has no recycle bin, and it is your colleagues' data too. Without it the removal is refused there (exit 3). Never implied: ask the user first.",
    };

    private static Command RelationChange(GlobalOptions options, bool remove)
    {
        var parent = new Argument<string>("parent") { Description = $"The product, bundle, package or node. {Refs}" };
        var child = new Argument<string>("child") { Description = $"The variant or entry. {Refs}" };
        var quantity = new Option<decimal?>("--quantity") { Description = "A bundle's or package's entry: how many (default 1 for a new one).", HelpName = "n" };
        var sortOrder = new Option<int?>("--sort-order") { Description = "Where it sorts among the parent's children (default 0 for a new one).", HelpName = "n" };
        var write = new WriteOptions();
        var allowDestructive = AllowDestructive();
        var command = new Command(remove ? "remove" : "add", remove
            ? """
              Remove a relation (Commerce's IRelationRepository): a product's variant, a bundle's or package's entry, an
              entry's extra category. Nothing to remove is `absent`. In a plan: {"op": "relation", "parent": "$shirt",
              "child": "$shirt-m", "remove": true}.
              Example: opticli commerce relation remove code:P-1001 code:P-1001-M
              """
            : """
              Relate catalog content (Commerce's IRelationRepository). Which relation follows from the two: a product and
              a variant (variant), a bundle or package and an entry (bundleEntry, packageEntry, with --quantity), a node and
              an entry (category: an extra node, besides the entry's own parent). Already there with the same quantity and
              sort order is `unchanged`. In a plan: {"op": "relation", "parent": "$shirt", "child": "$shirt-m"}.
              Example: opticli commerce relation add code:P-1001 code:P-1001-M
              """);
        command.Arguments.Add(parent);
        command.Arguments.Add(child);
        if (!remove)
        {
            command.Options.Add(quantity);
            command.Options.Add(sortOrder);
        }
        else
        {
            command.Options.Add(allowDestructive);
        }
        command.Options.Add(write.DryRun);
        WriteCommands.Mark(command);
        CommandRunner.SetHandler<RelationResult>(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var operation = new RelationOperation(parse.GetValue(parent)!, parse.GetValue(child)!, remove)
            {
                Quantity = remove ? null : parse.GetValue(quantity),
                SortOrder = remove ? null : parse.GetValue(sortOrder),
                AllowDestructive = remove && parse.GetValue(allowDestructive),
            };
            return WriteOptions.Result(await context.Writes(session).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }

    private static Command Relations(GlobalOptions options)
    {
        var reference = new Argument<string>("ref") { Description = Refs };
        var command = new Command("relations", """
            The relations of catalog content: children (a product's variants, a bundle's or package's entries, a node's
            extra entries) and parents (the products, bundles and packages it is in, the nodes it is an extra entry of),
            each with its ref, code, name, kind, quantity and sort order. An entry's own node is its parent, not listed here.
            Example: opticli commerce relations code:P-1001
            """);
        command.Arguments.Add(reference);
        CommandRunner.SetHandler<RelationList>(command, options, async (context, cancellationToken) =>
            new CommandResult<RelationList>(await ReadAsync<RelationList>(context, AgentRoutes.WithRef(AgentRoutes.CommerceRelations, context.Parse.GetValue(reference)!), cancellationToken), Source: WriteExecutor.AgentSource));
        return command;
    }

    private static Command Price(GlobalOptions options)
    {
        var command = new Command("price", "Set or remove one price of a variant or package.");
        command.Subcommands.Add(PriceChange(options, remove: false));
        command.Subcommands.Add(PriceChange(options, remove: true));
        return command;
    }

    private static Command PriceChange(GlobalOptions options, bool remove)
    {
        var entry = new Argument<string>("entry") { Description = $"The variant or package. {Refs}" };
        var market = new Option<string>("--market") { Description = "The market's id (DEFAULT; `opticli commerce markets`).", Required = true, HelpName = "id" };
        var currency = new Option<string>("--currency") { Description = "ISO 4217 code: USD, EUR, SEK.", Required = true, HelpName = "code" };
        var amount = new Option<decimal?>("--amount") { Description = "The unit price.", Required = !remove, HelpName = "n" };
        var priceType = new Option<string?>("--price-type") { Description = "Who it is for: all (default), user (a user name as --price-code), group (a customer price group as --price-code), or a price type of the site's own, by name or number (`opticli commerce` lists them as priceTypes).", HelpName = "type" };
        var priceCode = new Option<string?>("--price-code") { Description = "The user name or price group of a user or group price; for a type of the site's own, what the site keeps there (any text, or none).", HelpName = "code" };
        var minQuantity = new Option<decimal?>("--min-quantity") { Description = "It applies from this quantity on (default 0).", HelpName = "n" };
        var validFrom = new Option<string?>("--valid-from") { Description = "When it starts (UTC unless it has an offset), part of the key. Default: the price with the rest of the key that has no end (the base price); a new one starts today.", HelpName = "time" };
        var validUntil = new Option<string?>("--valid-until") { Description = "When it ends (UTC unless it has an offset); needs --valid-from. Default: no end.", HelpName = "time" };
        var write = new WriteOptions();
        var command = new Command(remove ? "remove" : "set", remove
            ? """
              Remove one price of a variant or package (Commerce's IPriceDetailService), named by its key as for set. Nothing
              to remove is `absent`. In a plan: {"op": "price", "entry": "$shirt-m", "market": "DEFAULT", "currency": "USD",
              "remove": true}.
              Example: opticli commerce price remove code:P-1001-M --market DEFAULT --currency USD
              """
            : """
              Set one price of a variant or package (Commerce's IPriceDetailService). A price is keyed by market, currency,
              price type and code, minimum quantity and valid-from: the row with that key is made or updated (amount,
              valid-until), and the entry's other prices stay. Already so is `unchanged`. Without --valid-from it is the base
              price (the one with no end); a campaign price is another row, with --valid-from and --valid-until. In a plan: {"op": "price", "entry": "$shirt-m", "market": "DEFAULT",
              "currency": "USD", "amount": 49.9}.
              Example: opticli commerce price set code:P-1001-M --market DEFAULT --currency USD --amount 49.90
              """);
        command.Arguments.Add(entry);
        command.Options.Add(market);
        command.Options.Add(currency);
        if (!remove)
        {
            command.Options.Add(amount);
        }
        command.Options.Add(priceType);
        command.Options.Add(priceCode);
        command.Options.Add(minQuantity);
        command.Options.Add(validFrom);
        var allowDestructive = AllowDestructive();
        if (!remove)
        {
            command.Options.Add(validUntil);
        }
        else
        {
            command.Options.Add(allowDestructive);
        }
        command.Options.Add(write.DryRun);
        WriteCommands.Mark(command);
        CommandRunner.SetHandler<PriceResult>(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var operation = new PriceOperation(parse.GetValue(entry)!, parse.GetValue(market)!, parse.GetValue(currency)!, remove ? null : parse.GetValue(amount))
            {
                PriceType = parse.GetValue(priceType),
                PriceCode = parse.GetValue(priceCode),
                MinQuantity = parse.GetValue(minQuantity),
                ValidFrom = Time(parse.GetValue(validFrom), "--valid-from"),
                ValidUntil = remove ? null : Time(parse.GetValue(validUntil), "--valid-until"),
                Remove = remove,
                AllowDestructive = remove && parse.GetValue(allowDestructive),
            };
            return WriteOptions.Result(await context.Writes(session).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }

    private static Command Prices(GlobalOptions options)
    {
        var entry = new Argument<string>("entry") { Description = Refs };
        var command = new Command("prices", """
            Every price of a catalog entry (Commerce's IPriceDetailService): market, currency, amount, price type and code,
            minimum quantity, valid from and until (UTC). Commerce keeps prices on variants and packages.
            Example: opticli commerce prices code:P-1001-M
            """);
        command.Arguments.Add(entry);
        CommandRunner.SetHandler<PriceList>(command, options, async (context, cancellationToken) =>
            new CommandResult<PriceList>(await ReadAsync<PriceList>(context, AgentRoutes.WithRef(AgentRoutes.CommercePrices, context.Parse.GetValue(entry)!), cancellationToken), Source: WriteExecutor.AgentSource));
        return command;
    }

    private static Command Warehouse(GlobalOptions options)
    {
        var command = new Command("warehouse", "Make or change a warehouse.");
        var code = new Argument<string>("code") { Description = "The warehouse's code (default is Commerce's own)." };
        var name = new Option<string?>("--name") { Description = "Its name (a new one's default: the code).", HelpName = "name" };
        var active = new Option<bool?>("--active") { Description = "Whether it is active (a new one is).", HelpName = "true|false" };
        var primary = new Option<bool?>("--primary") { Description = "Whether it is the primary warehouse.", HelpName = "true|false" };
        var fulfillment = new Option<bool?>("--fulfillment-center") { Description = "Whether it ships orders.", HelpName = "true|false" };
        var pickup = new Option<bool?>("--pickup-location") { Description = "Whether customers pick up orders there.", HelpName = "true|false" };
        var delivery = new Option<bool?>("--delivery-location") { Description = "Whether it delivers.", HelpName = "true|false" };
        var sortOrder = new Option<int?>("--sort-order") { Description = "Where it sorts among the warehouses.", HelpName = "n" };
        var write = new WriteOptions();
        var set = new Command("set", """
            Make a warehouse, or change one, by its code (Commerce's IWarehouseRepository). What isn't given keeps its value
            (a new one: active, not primary, no locations). Already so is `unchanged`; changes lists what changed. In a plan:
            {"op": "warehouse", "code": "oslo", "name": "Oslo"}.
            Example: opticli commerce warehouse set oslo --name Oslo --pickup-location true
            """);
        set.Arguments.Add(code);
        foreach (var option in new Option[] { name, active, primary, fulfillment, pickup, delivery, sortOrder, write.DryRun })
        {
            set.Options.Add(option);
        }
        WriteCommands.Mark(set);
        CommandRunner.SetHandler<WarehouseResult>(set, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var operation = new WarehouseOperation(parse.GetValue(code)!)
            {
                Name = parse.GetValue(name),
                Active = parse.GetValue(active),
                Primary = parse.GetValue(primary),
                FulfillmentCenter = parse.GetValue(fulfillment),
                PickupLocation = parse.GetValue(pickup),
                DeliveryLocation = parse.GetValue(delivery),
                SortOrder = parse.GetValue(sortOrder),
            };
            return WriteOptions.Result(await context.Writes(session).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        command.Subcommands.Add(set);
        return command;
    }

    private static Command Warehouses(GlobalOptions options)
    {
        var command = new Command("warehouses", """
            The site's warehouses (Commerce's IWarehouseRepository): code, name, active, primary, and what they are for.
            Example: opticli commerce warehouses
            """);
        CommandRunner.SetHandler<WarehouseList>(command, options, async (context, cancellationToken) =>
            new CommandResult<WarehouseList>(await ReadAsync<WarehouseList>(context, AgentRoutes.CommerceWarehouses, cancellationToken), Source: WriteExecutor.AgentSource));
        return command;
    }

    private static Command Inventory(GlobalOptions options)
    {
        var command = new Command("inventory", "Set a variant's or package's stock in a warehouse.");
        var entry = new Argument<string>("entry") { Description = $"The variant or package. {Refs}" };
        var warehouse = new Option<string>("--warehouse") { Description = "The warehouse's code (`opticli commerce warehouses`).", Required = true, HelpName = "code" };
        var inStock = new Option<decimal?>("--in-stock") { Description = "What can be bought now (Commerce's purchase available quantity).", HelpName = "n" };
        var backorder = new Option<decimal?>("--backorder") { Description = "What can be backordered.", HelpName = "n" };
        var preorder = new Option<decimal?>("--preorder") { Description = "What can be preordered.", HelpName = "n" };
        var tracked = new Option<bool?>("--tracked") { Description = "Whether stock is tracked (a new record is); untracked is always available.", HelpName = "true|false" };
        var write = new WriteOptions();
        var set = new Command("set", """
            Set a variant's or package's stock in a warehouse (Commerce's IInventoryService), one record per entry and
            warehouse. What isn't given keeps its value (a new record: 0, tracked). Already so is `unchanged`. In a plan:
            {"op": "inventory", "entry": "$shirt-m", "warehouse": "default", "inStock": 25}.
            Example: opticli commerce inventory set code:P-1001-M --warehouse default --in-stock 25
            """);
        set.Arguments.Add(entry);
        foreach (var option in new Option[] { warehouse, inStock, backorder, preorder, tracked, write.DryRun })
        {
            set.Options.Add(option);
        }
        WriteCommands.Mark(set);
        CommandRunner.SetHandler<InventoryResult>(set, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var operation = new InventoryOperation(parse.GetValue(entry)!, parse.GetValue(warehouse)!)
            {
                InStock = parse.GetValue(inStock),
                Backorder = parse.GetValue(backorder),
                Preorder = parse.GetValue(preorder),
                Tracked = parse.GetValue(tracked),
            };
            return WriteOptions.Result(await context.Writes(session).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        command.Subcommands.Add(set);
        return command;
    }

    private static Command Stock(GlobalOptions options)
    {
        var entry = new Argument<string>("entry") { Description = Refs };
        var command = new Command("stock", """
            A catalog entry's stock in every warehouse (Commerce's IInventoryService): in stock, backorder, preorder,
            tracked.
            Example: opticli commerce stock code:P-1001-M
            """);
        command.Arguments.Add(entry);
        CommandRunner.SetHandler<InventoryList>(command, options, async (context, cancellationToken) =>
            new CommandResult<InventoryList>(await ReadAsync<InventoryList>(context, AgentRoutes.WithRef(AgentRoutes.CommerceInventory, context.Parse.GetValue(entry)!), cancellationToken), Source: WriteExecutor.AgentSource));
        return command;
    }

    private static Command Market(GlobalOptions options)
    {
        var command = new Command("market", "Make, change or remove a market.");
        var id = new Argument<string>("id") { Description = "The market's id: 1 to 8 letters or digits (DEFAULT, US, NORDIC)." };
        var name = new Option<string?>("--name") { Description = "Its name (a new one's default: the id).", HelpName = "name" };
        var enabled = new Option<bool?>("--enabled") { Description = "Whether it is enabled (a new one is).", HelpName = "true|false" };
        var currencies = new Option<string?>("--currencies") { Description = "All its currencies, comma-separated ISO 4217 codes (replaces its list); a new market needs them.", HelpName = "USD,EUR" };
        var defaultCurrency = new Option<string?>("--default-currency") { Description = "One of its currencies. Default: the first.", HelpName = "code" };
        var languages = new Option<string?>("--languages") { Description = "All its languages, comma-separated (replaces its list); a new market needs them.", HelpName = "en,sv" };
        var defaultLanguage = new Option<string?>("--default-language") { Description = "One of its languages. Default: the first.", HelpName = "code" };
        var countries = new Option<string?>("--countries") { Description = "All its countries, comma-separated ISO 3166-1 alpha-3 codes (replaces its list).", HelpName = "USA,SWE" };
        var description = new Option<string?>("--description") { Description = "Its description (empty: none).", HelpName = "text" };
        var pricesIncludeTax = new Option<bool?>("--prices-include-tax") { Description = "Whether its prices include tax (a new one's don't).", HelpName = "true|false" };
        var write = new WriteOptions();
        var set = new Command("set", """
            Make a market, or change one, by its id (Commerce's IMarketService). What isn't given keeps its value; the
            lists given replace the market's, and an existing market's lists stay as they are (empty ones too) unless
            given. Commerce's save adds the default currency and language to the lists. Already so is `unchanged`; changes
            lists what changed. In a plan: {"op": "market", "market": "NORDIC", "currencies": ["SEK", "NOK"], "languages":
            ["en", "sv"], "countries": ["SWE", "NOR"]}.
            Example: opticli commerce market set NORDIC --name Nordic --currencies SEK,NOK --languages en,sv --countries SWE,NOR
            """);
        set.Arguments.Add(id);
        foreach (var option in new Option[] { name, enabled, currencies, defaultCurrency, languages, defaultLanguage, countries, description, pricesIncludeTax, write.DryRun })
        {
            set.Options.Add(option);
        }
        WriteCommands.Mark(set);
        CommandRunner.SetHandler<MarketResult>(set, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var operation = new MarketOperation(parse.GetValue(id)!)
            {
                Name = parse.GetValue(name),
                Enabled = parse.GetValue(enabled),
                Currencies = List(parse.GetValue(currencies)),
                DefaultCurrency = parse.GetValue(defaultCurrency),
                Languages = List(parse.GetValue(languages)),
                DefaultLanguage = parse.GetValue(defaultLanguage),
                Countries = List(parse.GetValue(countries)),
                Description = parse.GetValue(description),
                PricesIncludeTax = parse.GetValue(pricesIncludeTax),
            };
            return WriteOptions.Result(await context.Writes(session).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        command.Subcommands.Add(set);

        var removeId = new Argument<string>("id") { Description = "The market's id." };
        var removeWrite = new WriteOptions();
        var remove = new Command("remove", """
            Remove a market by its id (Commerce's IMarketService). Commerce removes the market's prices with it, in every
            catalog, and takes it off payment and shipping methods; orders and carts keep naming it. Commerce's own DEFAULT
            can't be removed (turn it off with market set --enabled false). Nothing to remove is `absent`. In a plan:
            {"op": "market", "market": "NORDIC", "remove": true}.
            Example: opticli commerce market remove NORDIC
            """);
        var removeAllowDestructive = AllowDestructive();
        remove.Arguments.Add(removeId);
        remove.Options.Add(removeAllowDestructive);
        remove.Options.Add(removeWrite.DryRun);
        WriteCommands.Mark(remove);
        CommandRunner.SetHandler<MarketResult>(remove, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var operation = new MarketOperation(parse.GetValue(removeId)!) { Remove = true, AllowDestructive = parse.GetValue(removeAllowDestructive) };
            return WriteOptions.Result(await context.Writes(session).RunAsync(operation, parse.GetValue(removeWrite.DryRun), cancellationToken));
        });
        command.Subcommands.Add(remove);
        return command;
    }

    private static Command Markets(GlobalOptions options)
    {
        var command = new Command("markets", """
            The site's markets (Commerce's IMarketService): id, name, enabled, currencies, languages, countries.
            Example: opticli commerce markets
            """);
        CommandRunner.SetHandler<MarketList>(command, options, async (context, cancellationToken) =>
            new CommandResult<MarketList>(await ReadAsync<MarketList>(context, AgentRoutes.CommerceMarkets, cancellationToken), Source: WriteExecutor.AgentSource));
        return command;
    }

    /// <summary>A read through the site's Commerce agent.</summary>
    internal static async Task<T> ReadAsync<T>(CliContext context, string route, CancellationToken cancellationToken)
    {
        var agent = await context.ConnectAgentAsync(cancellationToken);
        try
        {
            return await agent.SendAsync<T>(HttpMethod.Get, route, null, cancellationToken);
        }
        catch (NotFoundException ex) when (ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        {
            throw new NotFoundException(
                "The site's Commerce agent doesn't answer: the site has no Optimizely Commerce, or opticli's Commerce agent didn't load (or is older than this opticli).",
                "`opticli serve --status` shows commerce (agent false: error says why); restart the site with `opticli serve --stop`, then `opticli serve`.");
        }
    }

    private static DateTimeOffset? Time(string? text, string option) =>
        text is null ? null
        : PublishTimes.TryParse(text, out var at) ? at
        : throw new UsageException($"{option} '{text}' is not a time: give a date and time, e.g. 2026-12-01T00:00:00Z (UTC unless it has an offset).");

    private static IReadOnlyList<string>? List(string? text) =>
        text?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
