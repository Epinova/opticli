using System.Text.Json.Nodes;

namespace OptiCli.Integration.Schema;

/// <summary>
/// Every read command, in every mode, against the site under test: content found from the site's own output (its start
/// page, a page, a block, media, a folder, something in the recycle bin, a draft, a project, a job, CMS 13's Visual
/// Builder content), so it runs on any Alloy-based site, CMS 12 or 13. Reads only: it never starts or stops the site.
/// </summary>
internal static class SchemaReads
{
    /// <summary>Commands and modes this battery doesn't run, and why. Every other mode that doesn't write must print a result.</summary>
    public static readonly IReadOnlyDictionary<string, string> NotRun = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["serve [--foreground]"] = "runs until stopped",
        ["apply [--seal]"] = "runs a sealed plan against production: only the user, at a terminal",
        ["serve [otherwise]"] = "starts the site; SchemaWrites checks it on a running one",
        ["skill install [otherwise]"] = "writes the skill into the user's .claude folder: SchemaLocal, with a home of its own",
        ["db use [otherwise]"] = "changes the user config: SchemaLocal, in a sandbox",
        ["db add [otherwise]"] = "changes the user config: SchemaLocal",
        ["db remove [otherwise]"] = "changes the user config: SchemaLocal",
        ["db role [otherwise]"] = "changes the user config: SchemaLocal",
        ["db forget [otherwise]"] = "changes the user config: SchemaLocal",
        ["ide on [otherwise]"] = "changes the project's files: SchemaLocal",
        ["ide off [otherwise]"] = "changes the project's files: SchemaLocal",
    };

    /// <returns>The modes this site can't show (no projects, say), each with why.</returns>
    public static async Task<Dictionary<(string Command, string Mode), string>> RunAsync(OutputCheck check, string scratch)
    {
        var unavailable = new Dictionary<(string Command, string Mode), string>();
        var serveStatus = await check.Ok("serve", options: ["--status"]);
        var state = serveStatus?["state"]?.GetValue<string>();
        var running = state == "running";
        await CommerceAsync(check, running && serveStatus?["commerce"]?["agent"]?.GetValue<bool>() == true, unavailable);

        // The site, its languages and content model.
        await check.Ok("doctor");
        await check.Ok("db list");
        await check.Ok("ide status");
        await check.Ok("drift");
        if (running)
        {
            await check.Ok("users roles");
        }
        else
        {
            unavailable[("users roles", "otherwise")] = "asks the site, which isn't running";
        }
        var sites = await check.Ok("sites") as JsonArray ?? [];
        await check.Paged("sites");
        var site = sites.FirstOrDefault(s => s?["isDefault"]?.GetValue<bool>() != false) ?? sites.FirstOrDefault();
        var start = Text(site?["startPage"]) ?? "1";
        var assets = Text(site?["assetsRoot"]);
        var languages = await check.Ok("languages") as JsonArray ?? [];
        await check.Ok("languages", options: ["--all"]);
        await check.Paged("languages", options: ["--all"]);
        var other = languages.Select(l => Text(l?["code"])).FirstOrDefault(c => c is not null && c != Text(site?["masterLanguage"]));

        var types = await check.Ok("types") as JsonArray ?? [];
        await check.Paged("types");
        await check.Ok("types", options: ["--unused"]);
        await check.Ok("types", options: ["--orphaned"]);
        foreach (var sort in check.Schema.OptionValues("types", "--sort"))
        {
            await check.Ok("types", options: ["--sort", sort]);
        }
        var byKind = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kind in check.Schema.OptionValues("types", "--kind"))
        {
            var ofKind = await check.Ok("types", options: ["--kind", kind]) as JsonArray ?? [];
            byKind[kind] = ofKind.Where(t => t?["instances"]?.GetValue<int>() > 0).Select(t => Text(t?["name"])!).ToList();
            foreach (var typeName in byKind[kind].Take(2))
            {
                await check.Ok("type", [typeName]);
                await check.Ok("allowed-in", [typeName]);
            }
        }
        foreach (var kind in check.Schema.OptionValues("allowed-in", "--kind"))
        {
            if (byKind.GetValueOrDefault("block")?.FirstOrDefault() is { } blockType)
            {
                await check.Ok("allowed-in", [blockType], ["--kind", kind]);
            }
        }
        if (byKind.GetValueOrDefault("block")?.FirstOrDefault() is { } explicitBlock)
        {
            await check.Ok("allowed-in", [explicitBlock], ["--explicit"]);
            await check.Paged("allowed-in", [explicitBlock]);
        }
        await check.Ok("display-templates");
        await check.Paged("display-templates");
        // Only types that sit in a composition (CMS 13) have display templates of their own.
        foreach (var type in new[] { "experience", "section", "element" }.SelectMany(k => byKind.GetValueOrDefault(k) ?? []).Take(3))
        {
            await check.Ok("display-templates", options: ["--type", type]);
        }
        await check.Ok("categories");
        await check.Paged("categories");
        await check.Ok("visitor-groups");
        await check.Paged("visitor-groups");

        // Content: one of each kind.
        var page = First(await check.Ok("children", [start]));
        await check.Paged("children", [start]);
        await check.Ok("children", ["1"], ["--blueprints"]);
        var block = await FirstOfKind(check, byKind, "block");
        var media = await FirstOfKind(check, byKind, "media");
        var experience = await FirstOfKind(check, byKind, "experience");
        var element = await FirstOfKind(check, byKind, "element");
        var trashed = First(await check.Ok("trash"));
        await check.Paged("trash");
        var drafts = await check.Ok("drafts") as JsonArray ?? [];
        await check.Paged("drafts");
        foreach (var kind in check.Schema.OptionValues("drafts", "--kind"))
        {
            await check.Ok("drafts", options: ["--kind", kind]);
        }
        await check.Ok("drafts", options: ["--blueprints"]);
        var draftVersion = Text(drafts.FirstOrDefault()?["version"]);

        foreach (var reference in new[] { "1", start, page, block, media, assets, trashed, draftVersion, experience, element }.OfType<string>().Distinct())
        {
            await check.Ok("get", [reference]);
            await check.Ok("url", [reference]);
            await check.Ok("versions", [reference]);
            await check.Ok("history", [reference]);
            await check.Ok("access", [reference]);
            await check.Ok("where-used", [reference]);
            await check.Ok("where-used", [reference], ["--pages"]);
            await check.Ok("tree", [reference], ["--depth", "2"]);
            await check.Ok("ancestors", [reference]);
        }
        await check.Ok("get", [start], ["--full", "--all-properties"]);
        await check.Ok("get", [start], ["--expand", "--version", "latest"]);
        await check.Ok("get", [start], ["--fields", "Name"]);
        if (other is not null)
        {
            await check.Any("get", [start], ["--lang", other]);
            await check.Any("url", [start], ["--lang", other]);
        }
        foreach (var reference in new[] { page, experience }.OfType<string>())
        {
            await check.Ok("get", [reference], ["--full", "--all-properties", "--expand"]);
        }
        if (experience is not null)
        {
            // A Visual Builder experience with a variation, where the fixture has one.
            foreach (var variation in new[] { "vbFixtureVariation", "vbFixtureDraft" })
            {
                await check.Any("get", [experience], ["--variation", variation, "--version", "latest"]);
            }
        }
        await check.Paged("versions", [start]);
        await check.Paged("history", [start]);
        await check.Ok("tree", ["1"], ["--depth", "2", "--blueprints"]);
        if (media is not null)
        {
            await check.Ok("blob", [media]);
        }

        // URLs.
        var urls = await check.Ok("url", [start]);
        await check.Ok("resolve", ["/"]);
        foreach (var url in new[] { Text(urls?["url"]), Text(site?["url"]), media is null ? null : Text((await check.Ok("url", [media]))?["url"]) }.OfType<string>())
        {
            await check.Ok("resolve", [url]);
        }
        await check.Any("resolve", ["http://unknown.example/en/"]);

        // Finding content.
        var pageType = page is null ? null : Text((await check.Ok("get", [page], ["--fields", "Name"]))?["type"]);
        if (pageType is not null)
        {
            await check.Ok("find", options: ["--type", pageType]);
            await check.Paged("find", options: ["--type", pageType]);
            await check.Ok("find", options: ["--type", pageType, "--under", start, "--where", "Name~a"]);
            foreach (var status in check.Schema.OptionValues("find", "--status"))
            {
                await check.Ok("find", options: ["--type", pageType, "--status", status]);
            }
        }
        // A type with a draft: rows for find --status draft.
        if (Text(drafts.FirstOrDefault()?["type"]) is { } draftType)
        {
            await check.Ok("find", options: ["--type", draftType, "--status", "draft"]);
        }
        foreach (var type in byKind.Values.SelectMany(t => t).Take(12))
        {
            await check.Ok("find", options: ["--type", type, "--blueprints"]);
        }
        var name = Text((await check.Ok("get", [start], ["--fields", "Name"]))?["name"]) ?? "a";
        await check.Ok("search", [name]);
        await check.Paged("search", [name]);
        foreach (var scope in check.Schema.OptionValues("search", "--in"))
        {
            await check.Ok("search", [name], ["--in", scope]);
        }
        await check.Ok("search", ["@" + name]);

        // Where a type is used: instances, and CMS 13's inline rows.
        foreach (var type in new[] { "block", "element", "media" }.SelectMany(k => byKind.GetValueOrDefault(k) ?? []).Take(6))
        {
            await check.Ok("where-used", options: ["--type", type]);
        }
        if (block is not null)
        {
            await check.Paged("where-used", [block]);
        }

        // Projects, jobs and the rest.
        var projects = await check.Ok("projects") as JsonArray ?? [];
        await check.Paged("projects");
        foreach (var project in projects.Take(3))
        {
            await check.Ok("projects", [project!["id"]!.ToString()]);
        }
        if (projects.Count == 0)
        {
            unavailable[("projects", "<project>")] = "the site has no projects";
        }
        var jobs = await check.Ok("jobs") as JsonArray ?? [];
        await check.Paged("jobs");
        foreach (var filter in new[] { "--all", "--enabled", "--disabled", "--failed" })
        {
            await check.Ok("jobs", options: [filter]);
        }
        await check.Ok("jobs log");
        await check.Paged("jobs log");
        await check.Ok("jobs log", options: ["--failed"]);
        foreach (var job in jobs.Take(4))
        {
            await check.Ok("jobs log", [Text(job?["id"]) ?? Text(job?["name"])!]);
        }
        await check.Ok("sql", ["SELECT TOP 3 pkID, ContentGUID, fkContentTypeID, ContentPath, NULL AS Nothing, 1.5 AS Number, CAST(1 AS bit) AS Flag FROM tblContent"]);
        await check.Ok("sql", ["SELECT TOP 2 * FROM tblContentLanguage"], ["--full"]);
        await check.Ok("sql", ["SELECT pkID FROM tblContent"], ["--limit", "2"]);
        if (page is not null)
        {
            await check.Ok("export", [page], ["--out", Path.Combine(scratch, "export-page"), "--no-media"]);
            await check.Ok("export", [start], ["--out", Path.Combine(scratch, "export-tree"), "--descendants", "--all-languages"]);
        }

        // serve's other modes: logs, and a stop with nothing running (never a site's: one that is starting, paused in a
        // debugger or runs against another database has a process too).
        await check.Any("serve", options: ["--logs", "--tail", "5"]);
        if (state == "stopped")
        {
            await check.Ok("serve", options: ["--stop"]);
        }
        else if (running)
        {
            await check.Ok("serve");
        }
        else
        {
            unavailable[("serve", "--stop")] = $"the site's agent is {state}: a stop could end a process";
        }

        // Errors: the error envelope.
        await check.Fails("get", ["999999999"], code: "not_found");
        await check.Fails("find", code: "usage");
        await check.Fails("type", ["NoSuchTypeAnywhere"], code: "not_found");
        await check.Fails("get", [start], ["--version", "nope"]);
        foreach (var ((command, mode), why) in unavailable)
        {
            check.Note($"not on this site: {command} [{mode}]: {why}");
        }
        return unavailable;
    }

    /// <summary>
    /// The Commerce reads, on a Commerce site whose Commerce agent runs: the catalogs, and a variant the site has (the
    /// Commerce tests make some). Elsewhere they can't print a result, so they are unavailable.
    /// </summary>
    private static async Task CommerceAsync(OutputCheck check, bool commerce, Dictionary<(string Command, string Mode), string> unavailable)
    {
        string[] reads = ["commerce", "commerce get", "commerce children", "commerce tree", "commerce relations", "commerce prices", "commerce stock", "commerce warehouses", "commerce markets"];
        if (!commerce)
        {
            foreach (var read in reads)
            {
                unavailable[(read, "otherwise")] = "asks the site's Commerce agent: not a Commerce site, or not running";
            }
            return;
        }
        await check.Ok("commerce");
        await check.Ok("commerce markets");
        await check.Ok("commerce warehouses");
        await check.Ok("commerce children");
        var tree = await check.Ok("commerce tree", options: ["--depth", "3"]) as JsonArray ?? [];
        var variant = tree.Select(i => (Kind: Text(i?["kind"]), Ref: Text(i?["ref"]))).FirstOrDefault(i => i.Kind == "variation").Ref;
        var node = tree.Select(i => (Kind: Text(i?["kind"]), Ref: Text(i?["ref"]))).FirstOrDefault(i => i.Kind == "node").Ref;
        if (node is not null)
        {
            await check.Ok("commerce children", [node], ["--limit", "5"]);
        }
        if (variant is null)
        {
            foreach (var read in reads.Where(r => r is "commerce get" or "commerce relations" or "commerce prices" or "commerce stock"))
            {
                unavailable[(read, "otherwise")] = "the site's catalog has no variant in its first three levels";
            }
            return;
        }
        await check.Ok("commerce get", [variant]);
        await check.Ok("commerce relations", [variant]);
        await check.Ok("commerce prices", [variant]);
        await check.Ok("commerce stock", [variant]);
        await check.Fails("commerce get", ["code:opticli-no-such-code"], code: "not_found");
    }

    /// <summary>The modes the battery must have checked: every one that doesn't write, but those in <see cref="NotRun"/>.</summary>
    public static IEnumerable<(string Command, string Mode)> Required(OutputCheck check, bool running, IReadOnlyDictionary<(string Command, string Mode), string> unavailable) =>
        check.Schema.Modes()
            .Where(m => !m.Writes)
            .Select(m => (m.Command, m.Mode))
            .Where(m => !unavailable.ContainsKey(m))
            .Where(m => !NotRun.ContainsKey($"{m.Command} [{m.Mode}]") || (running && m == ("serve", "otherwise")))
            .Where(m => !(running && m == ("serve", "--stop")))
            // The logs mode prints only when a log exists.
            .Where(m => m != ("serve", "--logs"));

    private static async Task<string?> FirstOfKind(OutputCheck check, Dictionary<string, List<string>> byKind, string kind)
    {
        foreach (var type in byKind.GetValueOrDefault(kind) ?? [])
        {
            if (First(await check.Ok("find", options: ["--type", type, "--limit", "1"])) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static string? First(JsonNode? list) => list is JsonArray { Count: > 0 } array ? Text(array[0]?["ref"]) : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToString();
}
