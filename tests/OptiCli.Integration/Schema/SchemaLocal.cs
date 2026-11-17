using System.Text.Json.Nodes;
using OptiCli.Core;
using OptiCli.Core.Configuration;
using OptiCli.Core.Discovery;

namespace OptiCli.Integration.Schema;

/// <summary>
/// The commands that change the user's opticli config or the project's files, in a sandbox: an empty user config of its
/// own (<c>XDG_CONFIG_HOME</c>; the serve state is the real one, so a running agent is found). <c>db</c>, <c>sites
/// primary --forget</c>, <c>apply --seal-for</c> (another local database added as a prod database), and, on a copy of the
/// project with a state directory of its own, <c>ide on|off</c> and <c>env --json</c> (then stopped again); <c>skill
/// install</c> with a home of its own.
/// </summary>
internal static class SchemaLocal
{
    /// <summary>
    /// A database on the site's server that <c>apply --seal-for</c> may read as production (read only): another copy of the
    /// site's database, e.g. the edge-case site's own for its projects copy. Without it, sealing isn't checked.
    /// </summary>
    public const string SealDatabaseVariable = "OPTICLI_IT_SEAL_DB";

    public static string? SealDatabase => Environment.GetEnvironmentVariable(SealDatabaseVariable) is { Length: > 0 } name ? name : null;

    /// <returns>Whether a saved sites mapping could be made and forgotten (a site whose primary host it already is).</returns>
    public static async Task<bool> RunAsync(OutputCheck sandboxed, string scratch)
    {
        var check = sandboxed;
        var forgot = false;
        var status = await check.Ok("serve", options: ["--status"]);
        var running = status?["state"]?.GetValue<string>() == "running";

        var list = await check.Ok("db list");
        var id = Text((list?["choices"] as JsonArray)?.FirstOrDefault()?["id"]);
        if (id is not null)
        {
            await check.Ok("db use", [id]);
            await check.Ok("db forget");
            await check.Ok("db use", [id]);
        }
        // A saved mapping to forget: the primary host a site has already (unchanged, so the site isn't written), saved.
        if (running)
        {
            var sites = await check.Ok("sites") as JsonArray ?? [];
            foreach (var site in sites.OfType<JsonObject>())
            {
                var hosts = (site["hosts"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                if (hosts.Count(h => Text(h["type"]) == "primary") != 1 || hosts.Any(h => Text(h["type"]) == "edit" || h["language"] is not null))
                {
                    continue;
                }
                var pair = $"{Text(site["name"])}={Text(hosts.Single(h => Text(h["type"]) == "primary")["name"])}";
                var dry = await check.Ok("sites primary", [pair], ["--keep-edit", "--dry-run"]) as JsonArray;
                if (dry?.All(v => Text(v?["status"]) == "unchanged") != true)
                {
                    continue;
                }
                await check.Ok("sites primary", [pair], ["--keep-edit", "--save"]);
                await check.Ok("sites primary", options: ["--forget", Text(site["name"])!]);
                forgot = true;
                break;
            }
        }
        await check.Fails("sites primary", options: ["--forget", "No such site"]);

        // skill install writes to ~/.claude/skills/opticli/: a home of its own.
        var ownHome = check.With(check.Project, new Dictionary<string, string?>(check.Environment ?? new Dictionary<string, string?>())
        {
            ["HOME"] = Directory.CreateDirectory(Path.Combine(scratch, "home")).FullName,
        });
        await ownHome.Ok("skill install");
        await ownHome.Ok("skill install", options: ["--force"]);

        // IDE mode and env change the project's files and opticli's state (a .csproj.user, .git/info/exclude, the agent
        // copies, which ide on also prunes; the serve state): on a copy of the project, with a state directory of its own,
        // so the site's project and the user's IDE mode and running sites are never touched. ide on/off, then env, which
        // registers the copy as an external site, stopped again.
        var copy = Directory.CreateDirectory(Path.Combine(scratch, "project-copy")).FullName;
        foreach (var file in Directory.EnumerateFiles(check.Project, "*.csproj").Concat(Directory.EnumerateFiles(check.Project, "appsettings*.json")))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), overwrite: true);
        }
        var isolated = check.With(copy, new Dictionary<string, string?>(check.Environment ?? new Dictionary<string, string?>())
        {
            ["XDG_STATE_HOME"] = Directory.CreateDirectory(Path.Combine(scratch, "state")).FullName,
        });
        if ((await isolated.Ok("ide status"))?["on"]?.GetValue<bool>() == false)
        {
            await isolated.Ok("ide on");
            await isolated.Ok("ide off");
        }
        await isolated.Ok("env", options: ["--json"]);
        await isolated.Ok("env", options: ["--format", "json"]);
        await isolated.Ok("serve", options: ["--status"]);
        await isolated.Ok("serve", options: ["--stop"]);

        // Another database by name, with a role; with OPTICLI_IT_SEAL_DB it is that database as prod, and a plan is sealed
        // for it. (A role belongs to the server and database, so the site's own database can't be the prod one: it would
        // then be refused as the development database.) The connection string is the site's, found the way the CLI finds
        // it, and never printed.
        var environment = OptiCliEnvironment.FromProcess();
        var project = ProjectLocator.Locate(check.Project, environment.CurrentDirectory);
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionResolver.Resolve(new ConnectionRequest(), project, environment).Require().Value);
        var role = "test";
        if (SealDatabase is { } seal)
        {
            connection.InitialCatalog = seal;
            role = "prod";
        }
        await check.Ok("db add", ["schemaother"], ["--role", role, "--connection", connection.ConnectionString]);
        await check.Ok("db list");
        await check.Ok("db role", ["schemaother", role]);
        if (running && SealDatabase is not null)
        {
            // Sealed for that prod database (read only): the plan checked against the site and against it; the seal is
            // written into the sandboxed config.
            var start = Text((await check.Ok("sites") as JsonArray)?.FirstOrDefault()?["startPage"]);
            var guid = start is null ? null : Text((await check.Ok("get", [start], ["--fields", "Name"]))?["guid"]);
            var name = start is null ? null : Text((await check.Ok("get", [start], ["--fields", "Name"]))?["name"]);
            // A sealed plan is a file (no stdin).
            var plan = Path.Combine(Directory.CreateDirectory(Path.Combine(scratch, "seal")).FullName, "plan.json");
            await File.WriteAllTextAsync(plan, $$$"""{"operations": [{"op": "set", "ref": "{{{guid}}}", "properties": {"MetaDescription": "sealed {{{name}}}"}}]}""");
            await check.Ok("apply", [plan], ["--dry-run", "--seal-for", "schemaother"]);
        }
        await check.Ok("db remove", ["schemaother"]);
        return forgot;
    }

    /// <summary>The modes this part must have checked.</summary>
    public static IEnumerable<(string Command, string Mode)> Required(bool running, bool forgot)
    {
        string[] always = ["db list", "db use", "db forget", "db add", "db role", "db remove"];
        foreach (var command in always)
        {
            yield return (command, "otherwise");
        }
        if (forgot)
        {
            yield return ("sites primary", "--forget");
        }
        if (running && SealDatabase is not null)
        {
            yield return ("apply", "--seal-for");
        }
        yield return ("skill install", "otherwise");
        yield return ("ide on", "otherwise");
        yield return ("ide off", "otherwise");
        yield return ("env", "--json | --format json");
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToString();
}
