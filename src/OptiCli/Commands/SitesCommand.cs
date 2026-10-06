using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Configuration;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;
using OptiCli.Core.Sites;
using OptiCli.Core.Text;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// Lists site definitions (a database read), and changes their host names through the site (<c>primary</c>,
/// <c>host add|remove</c>), typically to point a restored production database at localhost.
/// </summary>
internal static class SitesCommand
{
    public static Command Create(GlobalOptions options)
    {
        var list = new ListOptions(options);
        var command = new Command("sites", """
            List site definitions: name, URL, start page, master language and host names.
            `sites primary` and `sites host add|remove` change the hosts through the site (needs `opticli serve`), for a
            restored copy of a production database whose sites still have the production host names.
            Example: opticli sites
            """);
        list.AddTo(command);
        command.Subcommands.Add(Primary(options));
        command.Subcommands.Add(Host(options));

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var sites = await SiteReader.ListAsync(db, cancellationToken);
            return CommandResult.From(list.Apply(context.Parse, sites));
        });
        return command;
    }

    private static Command Primary(GlobalOptions options)
    {
        var pairs = new Argument<string[]>("pairs")
        {
            Description = $"Site and host: {PrimaryPairs.Syntax}. The site is a name, id or GUID as `opticli sites` lists them; @lang makes the host primary for that language only.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var https = HttpsOption("Override the scheme in the pairs");
        var keepEdit = new Option<bool>("--keep-edit")
        {
            Description = "Leave the site's Edit host as it is. Default: make it undefined, since an Edit host on a production name sends the edit UI and preview there (with no Edit host the CMS edits on any host).",
        };
        var keepSiteUrl = new Option<bool>("--keep-site-url")
        {
            Description = "Leave the site's URL (SiteUrl) as it is. Default: a pair without @lang points it at the new primary host (https:// unless --https false).",
        };
        var fromConfig = new Option<bool>("--from-config")
        {
            Description = "Take the pairs from the mapping saved with --save, e.g. after the next database restore. Not with pairs.",
        };
        var save = new Option<bool>("--save")
        {
            Description = "After a successful run (not a dry run), save the pairs, with --keep-edit and --keep-site-url, as this project's mapping in the opticli user config, replacing the mapping's entries for those sites.",
        };
        var forget = new Option<string[]>("--forget")
        {
            Description = "Drop a site's entries from the saved mapping (a site that was deleted or renamed, say): its name, or one entry such as \"Site A@nb\". Repeatable; changes no site and needs no `serve`.",
            HelpName = "site",
            AllowMultipleArgumentsPerToken = false,
        };
        var write = new WriteOptions();
        var command = new Command("primary", $$"""
            Make a host each site's primary host, through the site (needs `opticli serve`): the host is added when the site
            doesn't have it, the previous primary host for that language and the site's Edit host become undefined, and a
            pair without @lang also sets the site's URL (SiteUrl), as does a @lang pair whose primary host SiteUrl was on
            (a pair without @lang whose host is a language's primary host already counts as that language's).
            A pair without @lang replaces the site's primary host: the one for every language, or, on a site whose only
            primary host is bound to a language, that one, in that language. Existing hosts are kept, so production URLs
            still resolve. All pairs are validated as one batch before any site is saved (exit 5 naming the pair), e.g. a host
            another site has; should the CMS still refuse a site while saving, the error lists the sites already saved.
            Safe to run again: a site already like that is `unchanged`. Refused against a shared database (exit 3).
            After a save, a site process other than the one `serve` runs keeps the old hosts until it restarts.
            meta.warnings names languages whose URLs still use a production host.
            Example: opticli sites primary "Site A=localhost:5001" "Site B=localhost:5002" --dry-run
            Example: opticli sites primary --from-config
            Example: opticli sites primary --forget "Old site"
            """);
        command.Arguments.Add(pairs);
        command.Options.Add(https);
        command.Options.Add(keepEdit);
        command.Options.Add(keepSiteUrl);
        command.Options.Add(fromConfig);
        command.Options.Add(save);
        command.Options.Add(forget);
        // No --accept-drift: drift is only checked against a shared database, where this is refused anyway.
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var given = parse.GetValue(pairs) ?? [];
            var useConfig = parse.GetValue(fromConfig);
            var saving = parse.GetValue(save);
            var dryRun = parse.GetValue(write.DryRun);
            if (parse.GetValue(forget) is { Length: > 0 } forgotten)
            {
                if (given.Length > 0 || useConfig || saving || dryRun || parse.GetValue(keepEdit) || parse.GetValue(keepSiteUrl) || parse.GetValue(https) is not null)
                {
                    throw new UsageException("--forget only changes the saved mapping; give it without pairs, --from-config, --save, --dry-run, --keep-edit, --keep-site-url or --https.");
                }
                return await ForgetAsync(context, forgotten, cancellationToken);
            }
            if (useConfig && given.Length > 0)
            {
                throw new UsageException("Give pairs or --from-config, not both.");
            }
            if (useConfig && saving)
            {
                throw new UsageException("--from-config applies the saved mapping; --save would only save it again.", "Leave out --save, or give the pairs to save instead of --from-config.");
            }
            if (!useConfig && given.Length == 0)
            {
                throw new UsageException("Give at least one pair, or --from-config.", $"Pairs are {PrimaryPairs.Syntax}.");
            }
            var httpsValue = ParseHttps(parse.GetValue(https));

            await using var session = await context.OpenContentAsync(cancellationToken);
            var sites = session.Model.Sites.All;
            var languages = EnabledLanguages(session);
            var warnings = new List<string>();
            IReadOnlyList<PrimaryPair> list;
            if (useConfig)
            {
                var mapping = UserConfig.ForProject(context.Environment.UserConfigFile, context.Project.Directory)?.PrimaryHosts
                    ?? throw new NotFoundException(
                        "This project has no saved sites mapping.",
                        "Save one with the pairs once: `opticli sites primary \"Site A=localhost:5001\" --save`.");
                list = PrimaryMapping.Pairs(mapping, sites, languages, httpsValue, parse.GetValue(keepEdit), parse.GetValue(keepSiteUrl), warnings);
            }
            else
            {
                list = given.Select(p => PrimaryPairs.Parse(p, sites, languages, httpsValue) with
                {
                    KeepEdit = parse.GetValue(keepEdit),
                    KeepSiteUrl = parse.GetValue(keepSiteUrl),
                }).ToList();
            }
            if (list.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            {
                throw new UsageException($"{twice.Key} is given {twice.Count()} times; a site has one primary host per language.");
            }

            var changes = list.Select(p => new SiteHostChange
            {
                Site = p.Site.Key,
                Host = p.Typed,
                Action = SiteHostActions.Primary,
                Language = p.Language,
                Https = p.Https,
                KeepEdit = p.KeepEdit,
                KeepSiteUrl = p.KeepSiteUrl,
            }).ToList();

            var result = await RunAsync(context, session, changes, dryRun, warnings, cancellationToken);
            if (saving && dryRun)
            {
                warnings.Add("Not saved to the sites mapping: --save saves after a real run, not a dry run.");
            }
            else if (saving)
            {
                UserConfig.SavePrimaryHosts(
                    context.Environment.UserConfigFile,
                    context.Project.Directory,
                    list.Select(p => p.Site.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    session.Model.Languages.Select(l => l.Code).ToList(),
                    list.Select(p => KeyValuePair.Create(p.Key, p.Saved)).ToList());
                warnings.Add($"Saved the mapping ({string.Join(", ", list)}) for this project; after the next database restore run `{PrimaryMapping.FromConfigCommand}`.");
            }
            return result;
        });
        return command;
    }

    /// <summary><c>--forget</c>: drops entries from the saved mapping, which the sites in the database may no longer have.</summary>
    private static async Task<CommandResult> ForgetAsync(CliContext context, IReadOnlyList<string> sites, CancellationToken cancellationToken)
    {
        var file = context.Environment.UserConfigFile;
        var mapping = UserConfig.ForProject(file, context.Project.Directory)?.PrimaryHosts
            ?? throw new NotFoundException("This project has no saved sites mapping.");
        // Every language branch, enabled or not: a key is Site@lang only for a real language code.
        await using var db = await context.OpenDatabaseAsync(cancellationToken);
        var languages = (await LanguageReader.ListAsync(db, cancellationToken)).Select(l => l.Code).ToList();
        var forgotten = new List<string>();
        foreach (var site in sites)
        {
            var removed = UserConfig.ForgetPrimaryHosts(file, context.Project.Directory, site.Trim(), languages);
            if (removed.Count == 0)
            {
                throw new NotFoundException(
                    $"The saved sites mapping has no entry for '{site.Trim()}'{(forgotten.Count > 0 ? $" (already dropped: {string.Join(", ", forgotten)})" : "")}.",
                    Suggestions.DidYouMean(site, mapping.Keys) ?? $"Its entries: {string.Join(", ", mapping.Keys)}.");
            }
            forgotten.AddRange(removed);
        }
        var left = UserConfig.ForProject(file, context.Project.Directory)?.PrimaryHosts;
        return new CommandResult(new { forgotten, mapping = left }, Source: CommandResult.CliSource);
    }

    private static Command Host(GlobalOptions options)
    {
        var host = new Command("host", """
            Add or remove one host name of a site, through the site (needs `opticli serve`). To make a host the primary
            host, use `opticli sites primary`.
            """);
        host.Subcommands.Add(HostAdd(options));
        host.Subcommands.Add(HostRemove(options));
        return host;
    }

    private static Command HostAdd(GlobalOptions options)
    {
        var site = SiteArgument();
        var name = HostArgument();
        var type = new Option<string>("--type")
        {
            Description = $"The host's type: {HostTypes.Syntax}. primary and edit make the site's previous one undefined.",
            HelpName = "type",
            DefaultValueFactory = _ => "undefined",
        };
        var lang = new Option<string?>("--lang") { Description = "The language the host is for (an enabled language); default every language.", HelpName = "code" };
        var https = HttpsOption("Whether the CMS links to the host with https (default: the scheme in <host>, else unset: the site URL's scheme)");
        var write = new WriteOptions();
        var command = new Command("add", """
            Add a host name to a site. Fails with a conflict (exit 5) if the site has it already, and with validation if
            another site has it. Against a shared database only --type undefined is allowed: it makes the site reachable
            locally without changing the URLs the deployed site generates.
            Example: opticli sites host add "Site A" localhost:5001 --dry-run
            """);
        command.Arguments.Add(site);
        command.Arguments.Add(name);
        command.Options.Add(type);
        command.Options.Add(lang);
        command.Options.Add(https);
        write.AddCommon(command, publish: false);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var typeName = HostTypes.Parse(parse.GetValue(type) ?? HostTypes.Undefined)
                ?? throw new UsageException($"--type '{parse.GetValue(type)}' is not a host type.", $"Host types: {HostTypes.Syntax}.");
            var given = ParseHttps(parse.GetValue(https));
            var (_, scheme) = Normalize(parse.GetValue(name)!, given);
            // Only a scheme says https here; the site reads a port alone (localhost:443) the same way for a new host.
            var httpsValue = given ?? (scheme is not null && parse.GetValue(name)!.Contains("://", StringComparison.Ordinal) ? HostHttps.Format(scheme) : null);

            await using var session = await context.OpenContentAsync(cancellationToken);
            var target = PrimaryPairs.RequireSite(parse.GetValue(site)!, session.Model.Sites.All);
            var change = new SiteHostChange
            {
                Site = target.Key,
                // As typed: the site normalises it the same way, with the https setting deciding which port is the default one.
                Host = parse.GetValue(name)!.Trim(),
                Action = SiteHostActions.Add,
                Type = typeName,
                Language = parse.GetValue(lang),
                Https = httpsValue,
            };
            return await RunAsync(context, session, [change], parse.GetValue(write.DryRun), [], cancellationToken);
        });
        return command;
    }

    private static Command HostRemove(GlobalOptions options)
    {
        var site = SiteArgument();
        var name = HostArgument();
        var write = new WriteOptions();
        var command = new Command("remove", """
            Remove one host name from a site. The site's last host is refused (exit 3), and so is the host of the site's URL
            (SiteUrl), which the CMS would add back: make another host primary first. Removing * warns that the site then
            only answers on its own hosts. Refused against a shared database (exit 3).
            Example: opticli sites host remove "Site A" localhost:5001 --dry-run
            """);
        command.Arguments.Add(site);
        command.Arguments.Add(name);
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            // As typed, not normalised: a host the site has as www.site.example:443 is removed by that name.
            var host = parse.GetValue(name)!.Trim();
            await using var session = await context.OpenContentAsync(cancellationToken);
            var target = PrimaryPairs.RequireSite(parse.GetValue(site)!, session.Model.Sites.All);
            var change = new SiteHostChange { Site = target.Key, Host = host, Action = SiteHostActions.Remove };
            return await RunAsync(context, session, [change], parse.GetValue(write.DryRun), [], cancellationToken);
        });
        return command;
    }

    /// <summary>Checks shared mode and drift, sends the changes, and shows each site's end state.</summary>
    /// <param name="warnings">The command's own warnings; the agent's are added after them.</param>
    private static async Task<CommandResult> RunAsync(CliContext context, ContentSession session, IReadOnlyList<SiteHostChange> changes, bool dryRun, List<string> warnings, CancellationToken cancellationToken)
    {
        var sites = session.Model.Sites.All;
        var shared = !session.Db.ConnectionString.IsLocal;
        SiteHostsRunner.RequireAllowed(changes, sites, shared);
        if (!dryRun)
        {
            await context.Writes(session).RequireDriftAcceptedAsync(cancellationToken);
        }
        var outcome = await SiteHostsRunner.RunAsync(await context.ConnectAgentAsync(cancellationToken), sites, changes, dryRun, shared, cancellationToken);
        warnings.InsertRange(0, outcome.Warnings);
        return new CommandResult(outcome.Sites, Text: Render(outcome.Sites), Warnings: warnings, Source: WriteExecutor.AgentSource);
    }

    private static string Render(IReadOnlyList<SiteHostsView> sites)
    {
        using var writer = new StringWriter();
        for (var i = 0; i < sites.Count; i++)
        {
            if (i > 0)
            {
                writer.WriteLine();
            }
            TextRenderer.Render(JsonOutput.ToNode(sites[i]), writer);
        }
        return writer.ToString();
    }

    private static Argument<string> SiteArgument() => new("site") { Description = "The site: its name, id or GUID, as `opticli sites` lists them." };

    private static Argument<string> HostArgument() => new("host") { Description = $"The host: {HostNames.Syntax}." };

    private static Option<string?> HttpsOption(string what) => new("--https")
    {
        Description = $"{what}: true, false, or unset (links use the site URL's scheme). Default: an existing host keeps its setting, a new one gets the pair's scheme, else unset.",
        HelpName = "true|false|unset",
    };

    private static string? ParseHttps(string? value) => value is null
        ? null
        : HostHttps.TryParse(value, out var https)
            ? HostHttps.Format(https)
            : throw new UsageException($"--https '{value}' is not true, false or unset.");

    private static (string Host, bool? Scheme) Normalize(string text, string? https = null) =>
        HostNames.TryNormalize(text, out var host, out var scheme, out var error, https is not null && HostHttps.TryParse(https, out var flag) ? flag : null)
            ? (host, scheme)
            : throw new UsageException(error, $"A host is {HostNames.Syntax}.");

    private static IReadOnlyCollection<string> EnabledLanguages(ContentSession session) =>
        session.Model.Languages.Where(l => l.Enabled && !l.IsInvariant).Select(l => l.Code).ToList();
}
