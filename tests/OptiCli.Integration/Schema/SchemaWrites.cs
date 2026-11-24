using System.Globalization;
using System.Text.Json.Nodes;

namespace OptiCli.Integration.Schema;

/// <summary>
/// Every write command, dry run and real, on the edge-case site (tests/fixtures/edge-cases) with its agent running: a
/// scratch page below the approval root's parent, with pages, a block, media, a translation, moves, deletes and restores
/// below it, access rights, projects (opticli 0.33 agents and newer), a plan, jobs, users, hosts, languages and, on CMS
/// 13, a Visual Builder composition. Everything it makes is deleted again (content to the recycle bin, as the other
/// write tests do), and every site setting it changes is put back.
/// </summary>
internal sealed class SchemaWrites(OutputCheck check, string scratch)
{
    /// <summary>Modes this battery doesn't run, and why. Every other mode that writes must print a result.</summary>
    public static readonly IReadOnlyDictionary<string, string> NotRun = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sync pull [otherwise]"] = "needs a second database to pull from (tests/fixtures/sync/sync-pull.sh)",
        ["sync push [otherwise]"] = "pushes to a production database",
        ["apply [--seal-for]"] = "seals for a prod database: SchemaLocal, in a sandboxed user config",
        ["sites primary [--forget]"] = "changes the user config: SchemaLocal",
        ["env [--json | --format json]"] = "registers the project as an external site: SchemaLocal, on a stopped site",
    };

    private readonly Stack<Func<Task>> undo = new();
    private readonly string stamp = Guid.NewGuid().ToString("N")[..8];

    private static readonly Guid ApprovalRoot = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02");

    /// <summary>The running agent's version (<c>serve --status</c>), which decides what it can do.</summary>
    public Version? Agent { get; private set; }

    public bool Projects => Agent is { } v && v >= new Version(0, 33, 0);

    public bool Cms13 { get; private set; }

    /// <summary>The site runs opticli's Commerce agent (<c>serve --status</c>'s <c>commerce.agent</c>): the <c>commerce</c> writes are checked.</summary>
    public bool Commerce { get; private set; }

    /// <summary>The site is the edge-case site, where every CMS write is checked; on a Commerce test site only the Commerce writes are.</summary>
    public bool EdgeCase { get; private set; }

    /// <returns>False when the site isn't the edge-case site (no approval root): nothing was written.</returns>
    public async Task<bool> RunAsync()
    {
        var status = await check.Ok("serve", options: ["--status"]);
        if (status?["state"]?.GetValue<string>() != "running")
        {
            check.Problem($"The write battery needs the site's agent running (`opticli serve --project {check.Project}`); serve --status says {status?["state"]}.");
            return false;
        }
        Agent = Version.TryParse(Text(status["agent"]?["version"])?.Split('-', '+')[0], out var version) ? version : null;
        Cms13 = Text(status["agent"]?["cmsVersion"])?.StartsWith("13", StringComparison.Ordinal) == true;
        Commerce = status["commerce"]?["agent"]?.GetValue<bool>() == true;
        check.Note($"agent {status["agent"]?["version"]}, CMS {status["agent"]?["cmsVersion"]}{(Commerce ? $", Commerce {status["commerce"]?["version"]}" : "")}");
        await check.Ok("serve");
        await check.Any("serve", options: ["--logs", "--tail", "5"]);

        // A Commerce test site has no approval root: its get is not_found there, which isn't a problem.
        var approval = Commerce ? (await check.Any("get", [ApprovalRoot.ToString()], ["--fields", "Name"])).Envelope?["data"] : await check.Ok("get", [ApprovalRoot.ToString()], ["--fields", "Name"]);
        EdgeCase = Text(approval?["parent"]) is not null;
        if (!EdgeCase && !Commerce)
        {
            check.Note("no approval root: not the edge-case site, nothing written");
            return false;
        }
        try
        {
            if (EdgeCase)
            {
                await ContentAsync(Text(approval!["parent"])!, Text(approval!["ref"])!);
                await SiteAsync();
                await JobsAndUsersAsync();
            }
            if (Commerce)
            {
                await CommerceAsync();
            }
        }
        finally
        {
            while (undo.TryPop(out var step))
            {
                try
                {
                    await step();
                }
                catch (Exception ex)
                {
                    check.Problem($"cleanup failed: {ex.Message}");
                }
            }
        }
        return true;
    }

    /// <summary>The modes the battery must have checked on this site: every one that writes, but those it can't run here.</summary>
    public IEnumerable<(string Command, string Mode)> Required() =>
        check.Schema.Modes()
            .Where(m => m.Writes)
            .Select(m => (m.Command, m.Mode))
            .Where(m => !NotRun.ContainsKey($"{m.Command} [{m.Mode}]"))
            .Where(m => Projects || !m.Command.StartsWith("projects", StringComparison.Ordinal))
            .Where(m => Cms13 || m.Command != "composition")
            // The Commerce writes need the Commerce agent; on a Commerce test site (not the edge-case site) only they run.
            .Where(m => IsCommerce(m.Command) ? Commerce : EdgeCase);

    private static bool IsCommerce(string command) => command == "commerce" || command.StartsWith("commerce ", StringComparison.Ordinal);

    /// <summary>
    /// Every Commerce write, dry run and real, on catalog content of its own (a catalog, a product and a variant with codes
    /// of this run), a warehouse and a market of its own. Catalog content can't be deleted with opticli, so it stays,
    /// as the Commerce integration tests' does; the price and relation are removed again, as the battery runs.
    /// </summary>
    private async Task CommerceAsync()
    {
        var catalog = $"opticli-it schema {stamp}";
        await check.Ok("create", ["catalog-root", "DefaultCurrency=USD", "DefaultLanguage=en", "WeightBase=kgs", "LengthBase=cm"], ["--type", "CatalogContent", "--name", catalog, "--publish"]);
        await check.Ok("create", [$"catalog:{catalog}", $"Code=schema-{stamp}-p"], ["--type", "FashionProduct", "--name", "Product", "--publish"]);
        await check.Ok("create", [$"catalog:{catalog}", $"Code=schema-{stamp}-v"], ["--type", "FashionVariant", "--name", "Variant", "--publish"]);
        await check.Ok("set", [$"code:schema-{stamp}-v", "Size=M"], ["--dry-run"]);
        string[] pair = [$"code:schema-{stamp}-p", $"code:schema-{stamp}-v"];
        await check.Ok("commerce relation add", pair, ["--dry-run"]);
        await check.Ok("commerce relation add", pair, ["--sort-order", "1"]);
        await check.Ok("commerce relation remove", pair, ["--dry-run"]);
        await check.Ok("commerce relation remove", pair);
        await check.Ok("commerce relation remove", pair);
        string[] price = ["--market", "DEFAULT", "--currency", "USD"];
        await check.Ok("commerce price set", [pair[1]], [.. price, "--amount", "10", "--dry-run"]);
        await check.Ok("commerce price set", [pair[1]], [.. price, "--amount", "10"]);
        await check.Ok("commerce price set", [pair[1]], [.. price, "--amount", "12"]);
        await check.Ok("commerce price remove", [pair[1]], [.. price, "--dry-run"]);
        await check.Ok("commerce price remove", [pair[1]], price);
        await check.Ok("commerce price remove", [pair[1]], price);
        var warehouse = $"schema-{stamp}";
        await check.Ok("commerce warehouse set", [warehouse], ["--name", "Schema", "--dry-run"]);
        await check.Ok("commerce warehouse set", [warehouse], ["--name", "Schema", "--pickup-location", "true"]);
        await check.Ok("commerce warehouse set", [warehouse], ["--active", "false"]);
        await check.Ok("commerce inventory set", [pair[1]], ["--warehouse", warehouse, "--in-stock", "3", "--dry-run"]);
        await check.Ok("commerce inventory set", [pair[1]], ["--warehouse", warehouse, "--in-stock", "3"]);
        await check.Ok("commerce inventory set", [pair[1]], ["--warehouse", warehouse, "--backorder", "2"]);
        var market = "S" + stamp[..7].ToUpperInvariant();
        await check.Ok("commerce market set", [market], ["--currencies", "USD", "--languages", "en", "--dry-run"]);
        await check.Ok("commerce market set", [market], ["--currencies", "USD", "--languages", "en", "--countries", "USA"]);
        await check.Ok("commerce market set", [market], ["--enabled", "false"]);
        await check.Fails("commerce price set", [pair[0]], [.. price, "--amount", "1"], code: "usage");
        await check.Fails("commerce inventory set", [$"code:schema-{stamp}-none"], ["--warehouse", "default"], code: "not_found");
    }

    private string Name(string what) => $"opticli-it schema {what} {stamp}";

    private async Task ContentAsync(string edge, string approvalRoot)
    {
        // The scratch container, published, which everything else goes below; to the recycle bin at the end.
        await check.Ok("create", [edge], ["--type", "StandardPage", "--name", Name("container"), "--dry-run"]);
        var container = Ref(await check.Ok("create", [edge], ["--type", "StandardPage", "--name", Name("container"), "--publish"]));
        if (container is null)
        {
            return;
        }
        undo.Push(() => check.Ok("delete", [container], ["--ignore-references"]));

        // Pages: a draft, a published one, values, a schedule.
        var draft = Ref(await check.Ok("create", [container, "MetaDescription=draft"], ["--type", "StandardPage", "--name", Name("draft")]))!;
        var page = Ref(await check.Ok("create", [container], ["--type", "StandardPage", "--name", Name("page"), "--publish", "--values", """{"MetaDescription": "published", "MetaKeywords": "a,b"}"""]))!;
        await check.Ok("set", [page, "MetaDescription=dry"], ["--dry-run"]);
        await check.Ok("set", [page, "MetaDescription=saved"]);
        await check.Ok("set", [page, "MetaDescription=from published"], ["--from", "published", "--dry-run"]);
        await check.Ok("publish", [page], ["--dry-run"]);
        await check.Ok("publish", [page]);
        await check.Ok("set", [draft, "MetaDescription=scheduled"], ["--publish-at", DateTimeOffset.UtcNow.AddDays(30).ToString("O", CultureInfo.InvariantCulture)]);
        await check.Ok("find", options: ["--type", "StandardPage", "--under", container, "--status", "scheduled"]);
        await check.Ok("set", [page, "MetaDescription=discard me"]);
        await check.Ok("discard", [page], ["--dry-run"]);
        await check.Ok("discard", [page]);
        var expired = Ref(await check.Ok("create", [container], ["--type", "StandardPage", "--name", Name("expired"), "--publish"]))!;
        await check.Ok("unpublish", [expired], ["--dry-run"]);
        await check.Ok("unpublish", [expired]);
        await check.Ok("find", options: ["--type", "StandardPage", "--under", container, "--status", "expired"]);
        await check.Ok("find", options: ["--type", "StandardPage", "--under", container, "--status", "draft"]);
        await check.Ok("drafts", options: ["--type", "StandardPage"]);

        // Below the approval sequence: a review request.
        var reviewed = Ref(await check.Ok("create", [approvalRoot], ["--type", "StandardPage", "--name", Name("review")]));
        if (reviewed is not null)
        {
            undo.Push(() => check.Ok("delete", [reviewed], ["--ignore-references"]));
            await check.Ok("set", [reviewed, "MetaDescription=for review"], ["--request-approval"]);
            await check.Any("publish", [reviewed], ["--dry-run"]);
        }

        // A block, an area, media.
        await check.Ok("block create", ["MainBody=<p>Dry</p>"], ["--type", "EditorialBlock", "--name", Name("teaser"), "--for", page, "--dry-run"]);
        var block = Ref(await check.Ok("block create", ["MainBody=<p>Hi</p>"], ["--type", "EditorialBlock", "--name", Name("teaser"), "--for", page, "--publish"])) ?? "1";
        await check.Ok("area", [page, "MainContentArea", "add", block], ["--dry-run"]);
        await check.Ok("area", [page, "MainContentArea", "add", block], ["--at", "0", "--publish"]);
        await check.Ok("area", [page, "MainContentArea", "add"], ["--type", "EditorialBlock", "--values", """{"MainBody": "<p>Inline</p>"}""", "--dry-run"]);
        await check.Ok("where-used", [block]);
        await check.Ok("where-used", [block], ["--pages"]);
        await check.Ok("where-used", options: ["--type", "EditorialBlock"]);
        await check.Ok("area", [page, "MainContentArea", "remove", "0"], ["--dry-run"]);
        var image = Path.Combine(scratch, $"schema-{stamp}.png");
        await File.WriteAllBytesAsync(image, OnePixelPng);
        await check.Ok("upload", [image], ["--for", page, "--dry-run"]);
        var media = Ref(await check.Ok("upload", [image, "Copyright=pixel"], ["--for", page, "--name", Name("pixel") + ".png", "--publish"]));
        if (media is not null)
        {
            await check.Ok("blob", [media]);
            await check.Ok("upload", [image], ["--replace", media, "--dry-run"]);
        }

        // A translation, and its removal.
        await check.Ok("translate", [page, "MetaDescription=sv"], ["--lang", "sv", "--name", Name("sv"), "--dry-run"]);
        await check.Ok("translate", [page, "MetaDescription=sv"], ["--lang", "sv", "--name", Name("sv"), "--with-blocks"]);
        await check.Ok("translate", [page], ["--lang", "sv", "--remove", "--confirm", "--dry-run"]);
        await check.Ok("translate", [page], ["--lang", "sv", "--remove", "--confirm"]);

        // Move, delete, restore.
        var moved = Ref(await check.Ok("create", [container], ["--type", "StandardPage", "--name", Name("moved"), "--publish"]))!;
        await check.Ok("move", [moved], ["--to", draft, "--dry-run"]);
        await check.Ok("move", [moved], ["--to", draft]);
        await check.Ok("delete", [moved], ["--dry-run"]);
        await check.Ok("delete", [moved]);
        await check.Ok("trash", options: ["--type", "StandardPage"]);
        await check.Ok("restore", [moved], ["--dry-run"]);
        await check.Ok("restore", [moved], ["--to", container]);

        // Access rights: shown, changed on the scratch page, back to inherited.
        await check.Ok("access", [moved]);
        await check.Ok("access", [moved], ["--break-inheritance", "--grant", "WebEditors=Read,Edit", "--dry-run"]);
        await check.Ok("access", [moved], ["--break-inheritance", "--grant", "WebEditors=Read,Edit"]);
        await check.Ok("access", [moved], ["--revoke", "WebEditors", "--dry-run"]);
        await check.Ok("access", [moved], ["--inherit"]);

        if (Projects)
        {
            await ProjectsAsync(container, page, draft);
        }
        await PlanAsync(container);
        if (Cms13)
        {
            await CompositionAsync(container);
        }
    }

    private async Task ProjectsAsync(string container, string page, string draft)
    {
        var name = Name("project");
        await check.Ok("projects create", [name], ["--dry-run"]);
        var project = Text((await check.Ok("projects create", [name]))?["project"]?["id"]);
        if (project is null)
        {
            return;
        }
        undo.Push(() => check.Any("projects delete", [project]));
        await check.Ok("set", [page, "MetaDescription=in project"], ["--in-project", name, "--dry-run"]);
        await check.Ok("set", [page, "MetaDescription=in project"], ["--in-project", project]);
        await check.Ok("set", [draft, "MetaDescription=ready in project"], ["--in-project", project, "--ready"]);
        var created = Ref(await check.Ok("create", [container], ["--type", "StandardPage", "--name", Name("in project"), "--in-project", project, "--ready"]));
        await check.Ok("block create", ["MainBody=<p>Project</p>"], ["--type", "EditorialBlock", "--name", Name("project teaser"), "--for", page, "--in-project", project, "--dry-run"]);
        await check.Ok("projects");
        await check.Ok("projects", [project]);
        await check.Ok("get", [page], ["--version", "latest"]);
        // Before anything is marked ready but the draft: what a publish would leave out.
        await check.Ok("projects publish", [project], ["--dry-run"]);
        await check.Ok("projects ready", [project, page], ["--dry-run"]);
        await check.Ok("projects ready", [project, page]);
        await check.Ok("projects ready", [project], ["--all", "--dry-run"]);
        if (created is not null)
        {
            await check.Ok("projects remove", [project, created], ["--dry-run"]);
            await check.Ok("projects remove", [project, created]);
        }
        await check.Ok("projects publish", [project], ["--dry-run"]);
        await check.Ok("projects publish", [project], ["--at", DateTimeOffset.UtcNow.AddDays(30).ToString("O", CultureInfo.InvariantCulture)]);
        await check.Ok("projects", [project]);
        await check.Ok("projects reactivate", [project], ["--dry-run"]);
        await check.Ok("projects reactivate", [project]);
        // Reactivated, its items are drafts again: marked ready once more, then published now.
        await check.Ok("projects ready", [project], ["--all"]);
        await check.Ok("projects publish", [project]);
        await check.Ok("projects", [project]);
        await check.Ok("projects delete", [project], ["--dry-run"]);
        await check.Ok("projects delete", [project]);
    }

    private async Task PlanAsync(string container)
    {
        var project = Projects ? $", \"project\": \"{Name("plan project")}\"" : "";
        var plan = $$$"""
            {"operations": [
              {"op": "create", "id": "page", "parent": "{{{container}}}", "type": "StandardPage", "name": "{{{Name("plan page")}}}", "properties": {"MetaDescription": "plan"}},
              {"op": "block", "id": "teaser", "type": "EditorialBlock", "name": "{{{Name("plan teaser")}}}", "for": "$page", "properties": {"MainBody": "<p>Plan</p>"}},
              {"op": "area", "ref": "$page", "property": "MainContentArea", "action": "add", "item": "$teaser"},
              {"op": "set", "ref": "$page", "properties": {"MetaKeywords": "plan"}},
              {"op": "translate", "ref": "$page", "lang": "sv", "name": "{{{Name("plan sv")}}}"}
            ]{{{project}}}}
            """;
        await check.Ok("apply", ["-"], ["--dry-run"], plan);
        var run = await check.Ok("apply", ["-"], [], plan);
        if (Text(run?["projects"]?[0]?["id"]) is { } made && run!["projects"]![0]!["status"]?.GetValue<string>() == "created")
        {
            undo.Push(() => check.Any("projects delete", [made]));
        }
        // Steps that don't save a version, with their results.
        if (Text(run?["created"]?["page"]) is { } planned)
        {
            var more = $$$"""
                {"operations": [
                  {"op": "publish", "ref": "{{{planned}}}", "lang": "en"},
                  {"op": "access", "ref": "{{{planned}}}", "grant": {"WebEditors": "Read"}, "breakInheritance": true},
                  {"op": "access", "ref": "{{{planned}}}", "inherit": true},
                  {"op": "move", "ref": "{{{planned}}}", "to": "{{{container}}}"},
                  {"op": "delete", "ref": "{{{planned}}}", "ignoreReferences": true},
                  {"op": "restore", "ref": "{{{planned}}}", "to": "{{{container}}}"},
                  {"op": "language", "lang": "sv"}
                ]}
                """;
            await check.Ok("apply", ["-"], ["--dry-run"], more);
            await check.Ok("apply", ["-"], [], more);
        }
        await check.Fails("apply", ["-"], ["--dry-run"], input: """{"operations": [{"op": "move", "ref": "1", "to": "2", "lang": "sv"}]}""");
    }

    private async Task CompositionAsync(string container)
    {
        var experiences = await check.Ok("types", options: ["--kind", "experience"]) as JsonArray ?? [];
        foreach (var type in experiences.Select(t => Text(t?["name"])).OfType<string>())
        {
            var existing = (await check.Ok("find", options: ["--type", type, "--limit", "1"]) as JsonArray)?.FirstOrDefault();
            if (existing is null)
            {
                continue;
            }
            var full = await check.Ok("get", [Text(existing["ref"])!], ["--fields", "composition"]);
            if (full?["composition"] is not JsonObject composition || Text(full["parent"]) is not { } parent)
            {
                continue;
            }
            var values = new JsonObject { ["composition"] = composition.DeepClone() }.ToJsonString();
            var made = Ref(await check.Ok("create", [parent], ["--type", type, "--name", Name("experience"), "--values", values]));
            if (made is null)
            {
                return;
            }
            undo.Push(() => check.Ok("delete", [made], ["--ignore-references"]));
            var nodes = Nodes((await check.Ok("get", [made], ["--fields", "composition", "--version", "latest"]))?["composition"]).ToList();
            var element = nodes.FirstOrDefault(n => n.Kind == "elements").Node;
            var column = nodes.LastOrDefault(n => n.Kind == "columns").Node;
            if (element is not null)
            {
                var key = Text(element["key"])!;
                await check.Ok("composition", [made, "set", key], ["--name", "Renamed", "--dry-run"]);
                await check.Ok("composition", [made, "set", key], ["--name", "Renamed"]);
                if (column is not null)
                {
                    await check.Ok("composition", [made, "move", key], ["--in", Text(column["key"])!, "--at", "0", "--dry-run"]);
                    await check.Any("composition", [made, "add", "element"], ["--in", Text(column["key"])!, "--type", Text(element["type"])!, "--dry-run"]);
                }
                await check.Ok("composition", [made, "remove", key], ["--dry-run"]);
            }
            var section = nodes.FirstOrDefault(n => n.Kind == "sections").Node;
            if (section is not null)
            {
                await check.Ok("composition", [made, "remove", Text(section["key"])!], ["--dry-run"]);
            }
            return;
        }
        check.Note("no experience with a composition: composition not checked");
    }

    private async Task SiteAsync()
    {
        // Languages: a dry run each way, and a disabled language with no content enabled and disabled again.
        var languages = await check.Ok("languages", options: ["--all"]) as JsonArray ?? [];
        var enabled = languages.Where(l => l?["enabled"]?.GetValue<bool>() == true).Select(l => Text(l?["code"])!).ToList();
        await check.Ok("languages enable", [.. enabled.Take(1)], ["--dry-run"]);
        await check.Any("languages disable", [.. enabled.Skip(1).Take(1).DefaultIfEmpty(enabled[0])], ["--dry-run"]);
        if (languages.FirstOrDefault(l => l?["enabled"]?.GetValue<bool>() == false && l?["contentItems"]?.GetValue<int>() == 0) is { } unused)
        {
            var code = Text(unused["code"])!;
            await check.Ok("languages enable", [code], ["--dry-run"]);
            undo.Push(() => check.Ok("languages disable", [code]));
            await check.Ok("languages enable", [code]);
        }

        // Hosts: on the edge-case site's hosts site B, added and removed again.
        var sites = await check.Ok("sites") as JsonArray ?? [];
        var hostsSite = sites.FirstOrDefault(s => Text(s?["name"]) == "Edge hosts B") ?? sites.FirstOrDefault();
        var siteName = Text(hostsSite?["name"])!;
        var host = $"schema-{stamp}.hosts-b.localhost";
        await check.Ok("sites host add", [siteName, host], ["--dry-run"]);
        undo.Push(() => check.Ok("sites host remove", [siteName, host]));
        await check.Ok("sites host add", [siteName, host], ["--type", "edit"]);
        await check.Ok("sites host remove", [siteName, host], ["--dry-run"]);
        await check.Ok("sites primary", [$"{siteName}={host}"], ["--dry-run"]);
        await check.Ok("sites add", [Name("site")], ["--start-page", Text(hostsSite?["startPage"])!, "--host", $"site-{stamp}.localhost", "--dry-run"]);

        // Content types: removals as dry runs (the orphans fixture's types, where there are any).
        var orphaned = await check.Ok("types", options: ["--orphaned"]) as JsonArray ?? [];
        // The first one nothing uses (a type that something still uses is a conflict).
        foreach (var orphan in orphaned.Select(t => Text(t?["name"])!).Append("EdgeRemovedEmptyPage").Distinct())
        {
            if ((await check.Any("types remove", [orphan], ["--dry-run"])).Ok)
            {
                break;
            }
        }
        await check.Any("types remove-property", ["EdgePage", "EdgeRemovedEmptyText"], ["--dry-run"]);
        await check.Ok("types prune", options: ["--dry-run"]);
        await check.Ok("types prune", options: ["--properties", "--dry-run"]);
    }

    private async Task JobsAndUsersAsync()
    {
        var jobs = await check.Ok("jobs", options: ["--all"]) as JsonArray ?? [];
        var job = Text(jobs.FirstOrDefault(j => Text(j?["name"]) == "opticli test job")?["name"]);
        if (job is not null)
        {
            await check.Ok("jobs run", [job], ["--dry-run"]);
            await check.Ok("jobs run", [job]);
            await check.Ok("jobs run", [job], ["--no-wait"]);
            await check.Ok("jobs stop", [job], ["--dry-run"]);
            await check.Any("jobs stop", [job]);
            await check.Ok("jobs set", [job], ["--every", "1h", "--next", "now", "--dry-run"]);
            await check.Ok("jobs set", [job], ["--enabled", "false", "--dry-run"]);
            await check.Ok("jobs log", [job]);
        }
        else
        {
            var any = Text(jobs.FirstOrDefault()?["name"])!;
            await check.Ok("jobs run", [any], ["--dry-run"]);
            await check.Ok("jobs stop", [any], ["--dry-run"]);
            await check.Ok("jobs set", [any], ["--every", "1h", "--next", "now", "--dry-run"]);
        }

        var user = $"opticli-it-schema-{stamp}";
        await check.Ok("users add", [user], ["--role", "WebEditors", "--dry-run"]);
        undo.Push(() => check.Any("users remove", [user]));
        await check.Ok("users add", [user], ["--role", "WebEditors", "--password-stdin"], $"Schema-{stamp}-{Guid.NewGuid():N}!\n");
        await check.Ok("users remove", [user], ["--dry-run"]);
        await check.Ok("users remove", [user]);
        await check.Ok("users roles");
    }

    /// <summary>A composition's nodes, each with the list it is in (<c>sections</c>, <c>rows</c>, <c>columns</c>, <c>elements</c>).</summary>
    private static IEnumerable<(string Kind, JsonObject Node)> Nodes(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            yield break;
        }
        foreach (var list in new[] { "sections", "rows", "columns", "elements", "nodes" })
        {
            foreach (var child in (obj[list] as JsonArray ?? []).OfType<JsonObject>())
            {
                yield return (list, child);
                foreach (var below in Nodes(child))
                {
                    yield return below;
                }
            }
        }
    }

    private static string? Ref(JsonNode? output) => Text(output?["ref"]);

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToString();

    /// <summary>A 1×1 PNG.</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
