using System.CommandLine;
using System.Text.Json;
using OptiCli.Cli;

namespace OptiCli.Tests;

/// <summary>
/// Environment roles at the command line: <c>db add</c>, <c>db role</c>, <c>db remove</c>, and a production database
/// read for a run but never chosen, written to or served, whichever way a command picks it. No database is connected
/// to: every refusal comes before that.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class EnvironmentRolesTests : IDisposable
{
    private const string ProdDatabase = "alloy-edge-roles-prod";
    private const string Prod = $"Server=localhost,1433;Database={ProdDatabase};User Id=sa;Password=hunter2-prod;TrustServerCertificate=True";
    private const string DevDatabase = RolesSite.DevDatabase;

    private readonly RolesSite _site = new();

    public void Dispose() => _site.Dispose();

    /// <summary>Each write command, with arguments it parses; <see cref="Every_write_command_is_in_the_list"/> keeps it complete.</summary>
    private static readonly string[] WriteLines =
    [
        "set 1 Heading=x",
        "create 1 --type StandardPage --name x",
        "area 1 MainContentArea add 2",
        "composition 1 remove node",
        "block create --type TeaserBlock --name x",
        "upload image.png",
        "translate 1 --lang sv",
        "publish 1",
        "unpublish 1",
        "discard 1",
        "move 1 --to 2",
        "delete 1",
        "restore 1",
        "access 1 --grant Everyone=Read",
        "apply plan.json",
        "sync pull --from other --all",
        "sync push --to other --all",
        "sites primary Site=localhost:5001",
        "sites host add Site localhost:5001",
        "sites host remove Site localhost:5001",
        "jobs run job",
        "jobs stop job",
        "jobs set job --enabled false",
        "users add dev",
        "users remove dev",
        "types remove OldPage",
        "types remove-property StandardPage Old",
        "types prune",
    ];

    public static TheoryData<string> WriteCommandLines() => new(WriteLines);

    [Fact]
    public void Every_write_command_is_in_the_list()
    {
        var root = Program.CreateRoot(new GlobalOptions());
        var marked = Paths(root, []).Where(p => WriteCommands.IsMarked(p.Command)).Select(p => p.Path).ToList();
        var unlisted = marked.Where(path => !WriteLines.Any(line => (line + " ").StartsWith(path + " ", StringComparison.Ordinal))).ToList();

        Assert.True(unlisted.Count == 0, $"Write commands missing from {nameof(WriteLines)}: {string.Join(", ", unlisted)}");
        Assert.Equal(WriteLines.Length, marked.Count);
    }

    [Fact]
    public void Access_without_changes_is_a_read()
    {
        var root = Program.CreateRoot(new GlobalOptions());

        Assert.False(WriteCommands.Writes(root.Parse("access 1")));
        Assert.True(WriteCommands.Writes(root.Parse("access 1 --inherit")));
        Assert.False(WriteCommands.Writes(root.Parse("get 1")));
    }

    [Theory]
    [MemberData(nameof(WriteCommandLines))]
    public async Task Every_write_command_refuses_production(string line)
    {
        await AddProd();

        var run = await _site.RunAsync([.. line.Split(' '), "--db", "prod"]);

        AssertProdRefused(run);
    }

    [Theory]
    [InlineData("serve")]
    [InlineData("serve --foreground")]
    [InlineData("env")]
    [InlineData("env --include-connection")]
    public async Task Serve_and_env_refuse_production(string line)
    {
        await AddProd();

        AssertProdRefused(await _site.RunAsync([.. line.Split(' '), "--db", "prod"]));
    }

    [Fact]
    public async Task Stopping_or_checking_the_site_is_never_refused()
    {
        await AddProd();

        var stop = await _site.RunAsync("serve", "--stop", "--db", "prod");
        var status = await _site.RunAsync("serve", "--status", "--db", "prod");

        Assert.True(stop.ExitCode == 0, stop.ToString());
        Assert.True(status.ExitCode == 0, status.ToString());
        Assert.Equal("stopped", status.Json().GetProperty("data").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Every_way_of_picking_production_is_refused_for_a_write()
    {
        await AddProd();
        _site.WriteProjectFile("appsettings.Production.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "Server=127.0.0.1;Database={{ProdDatabase}}" } }""");
        _site.WriteProjectFile("Properties/launchSettings.json", $$"""
            { "profiles": { "Prod": { "commandName": "Project", "environmentVariables": { "ConnectionStrings__EPiServerDB": "Server=(local);Database={{ProdDatabase}}" } } } }
            """);
        var choices = (await _site.RunAsync("db", "list")).Json().GetProperty("data").GetProperty("choices").EnumerateArray().ToList();
        var fileId = choices.Single(c => c.GetProperty("from").GetString() == "appsettings.Production.json").GetProperty("id").GetString()!;
        var namedId = choices.Single(c => c.TryGetProperty("name", out _)).GetProperty("id").GetString()!;
        var write = new[] { "set", "1", "Heading=x" };

        AssertProdRefused(await _site.RunAsync([.. write, "--db", "PROD"]));
        AssertProdRefused(await _site.RunAsync([.. write, "--db", namedId]));
        AssertProdRefused(await _site.RunAsync([.. write, "--db", fileId]));
        AssertProdRefused(await _site.RunAsync([.. write, "--db", ProdDatabase]));
        AssertProdRefused(await _site.RunAsync([.. write, "--profile", "Prod"]));
        AssertProdRefused(await _site.RunAsync([.. write, "--connection", $"Data Source=tcp:127.0.0.1,1433;Initial Catalog={ProdDatabase.ToUpperInvariant()}"]));
        using (_site.Variable("OPTICLI_DB", $"Server=.;Database={ProdDatabase}"))
        {
            AssertProdRefused(await _site.RunAsync(write));
        }
        using (_site.Variable("ConnectionStrings__EPiServerDB", $"Server=localhost;Database={ProdDatabase}"))
        {
            AssertProdRefused(await _site.RunAsync(write), "the site's Development configuration");
        }
        // The launch profile is the site's Development default too, once it is the only one.
        AssertProdRefused(await _site.RunAsync(write), "the site's Development configuration");
    }

    [Fact]
    public async Task A_saved_choice_that_became_production_is_refused_with_a_hint()
    {
        var dev = await DevId();
        Assert.Equal(0, (await _site.RunAsync("db", "use", dev)).ExitCode);
        Assert.Equal(0, (await _site.RunAsync("db", "role", dev, "prod")).ExitCode);

        var read = await _site.RunAsync("get", "1");
        var write = await _site.RunAsync("set", "1", "Heading=x");

        AssertProdRefused(read, "chosen with `opticli db use`", DevDatabase);
        AssertProdRefused(write, "chosen with `opticli db use`", DevDatabase);
        Assert.Contains("db forget", read.Error().GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Db_use_refuses_production()
    {
        await AddProd();

        var byName = await _site.RunAsync("db", "use", "prod");

        AssertProdRefused(byName, "make the development database");
        Assert.DoesNotContain("chosenVia", File.ReadAllText(_site.ConfigFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_of_production_says_so_in_meta_and_warnings()
    {
        await AddProd();

        var run = await RunWithProjectAsync((command, options) => CommandRunner.SetHandler(command, options, (context, _) =>
        {
            context.UseConnection();
            return Task.FromResult(new CommandResult("read"));
        }), "--db", "prod");

        Assert.True(run.ExitCode == 0, run.ToString());
        var meta = run.Json().GetProperty("meta");
        var database = meta.GetProperty("database");
        Assert.Equal("prod", database.GetProperty("role").GetString());
        Assert.Equal(ProdDatabase, database.GetProperty("name").GetString());
        Assert.False(database.GetProperty("development").GetBoolean());
        Assert.Contains("production database", meta.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_of_the_development_database_has_no_role_in_meta()
    {
        var run = await RunWithProjectAsync((command, options) => CommandRunner.SetHandler(command, options, (context, _) =>
        {
            context.UseConnection();
            return Task.FromResult(new CommandResult("read"));
        }));

        Assert.False(run.Json().GetProperty("meta").TryGetProperty("database", out _), run.ToString());
    }

    [Fact]
    public async Task Db_add_saves_the_entry_and_its_role_and_never_prints_the_password()
    {
        var add = await AddProd();
        var list = await _site.RunAsync("db", "list");

        var added = add.Json().GetProperty("data");
        Assert.Equal("prod", added.GetProperty("database").GetProperty("name").GetString());
        Assert.Equal("prod", added.GetProperty("database").GetProperty("role").GetString());
        Assert.Contains("only reads it", added.GetProperty("note").GetString(), StringComparison.Ordinal);
        var choice = list.Json().GetProperty("data").GetProperty("choices").EnumerateArray().Single(c => c.TryGetProperty("name", out _));
        Assert.Equal("prod", choice.GetProperty("role").GetString());
        Assert.DoesNotContain("hunter2", add.Stdout + list.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"role\": \"prod\"", File.ReadAllText(_site.ConfigFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("db add a71c3f --role prod", "usage")]
    [InlineData("db add has space --role prod", "usage")]
    [InlineData("db add prod --role production", "usage")]
    [InlineData("db add prod --role prod", "usage")]
    [InlineData("db add remote --role local --connection Server=sql.example.com;Database=Site", "usage")]
    [InlineData("db add broken --role prod --connection Server=", "usage")]
    public async Task Db_add_refuses_what_it_cant_save(string line, string code)
    {
        var run = await _site.RunAsync(line.Split(' '));

        Assert.Equal(code, run.Error().GetProperty("code").GetString());
        Assert.False(File.Exists(_site.ConfigFile) && File.ReadAllText(_site.ConfigFile).Contains("\"databases\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_weaker_role_is_refused_away_from_a_terminal_however_it_is_asked_for()
    {
        await AddProd();

        var role = await _site.RunAsync("db", "role", "prod", "test");
        var secondEntry = await _site.RunAsync("db", "add", "copy", "--role", "local", "--connection", $"Server=tcp:127.0.0.1,1433;Database={ProdDatabase}");

        Assert.All(new[] { role, secondEntry }, run =>
        {
            Assert.True(run.ExitCode == 3, run.ToString());
            Assert.Equal("roleLowered", run.Error().GetProperty("details").GetProperty("reason").GetString());
        });
        Assert.DoesNotContain("\"copy\"", File.ReadAllText(_site.ConfigFile), StringComparison.Ordinal);
        AssertProdRefused(await _site.RunAsync("set", "1", "Heading=x", "--db", "prod"));
    }

    [Fact]
    public async Task A_stronger_role_needs_no_terminal_and_local_is_only_for_this_machine()
    {
        var dev = await DevId();

        var test = await _site.RunAsync("db", "role", dev, "test");
        var prod = await _site.RunAsync("db", "role", DevDatabase, "prod");
        await AddProd();
        var remote = await _site.RunAsync("db", "add", "staging", "--role", "test", "--connection", "Server=sql.example.com;Database=Site;User Id=reader;Password=x");
        var local = await _site.RunAsync("db", "role", "staging", "local");

        Assert.True(test.ExitCode == 0, test.ToString());
        Assert.True(prod.ExitCode == 0, prod.ToString());
        Assert.Equal("test", prod.Json().GetProperty("data").GetProperty("previous").GetString());
        Assert.True(remote.ExitCode == 0, remote.ToString());
        Assert.Equal("usage", local.Error().GetProperty("code").GetString());
    }

    [Fact]
    public async Task Db_remove_keeps_the_role_of_the_server_and_database()
    {
        await AddProd();

        var remove = await _site.RunAsync("db", "remove", "prod");
        var again = await _site.RunAsync("db", "remove", "prod");
        var write = await _site.RunAsync("set", "1", "Heading=x", "--connection", Prod);

        Assert.True(remove.ExitCode == 0, remove.ToString());
        Assert.Equal("prod", remove.Json().GetProperty("data").GetProperty("roleKept").GetString());
        Assert.Equal("not_found", again.Error().GetProperty("code").GetString());
        AssertProdRefused(write);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("dev")]
    public async Task Sync_pull_writes_only_to_a_local_database(string role)
    {
        await AddProd();
        Assert.Equal(0, (await _site.RunAsync("db", "role", await DevId(), role)).ExitCode);

        var run = await _site.RunAsync("sync", "pull", "--from", "prod", "--all");

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Equal("syncTarget", run.Error().GetProperty("details").GetProperty("reason").GetString());
        Assert.Contains($"has the role {role}", run.Error().GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_pull_reads_production_but_never_writes_to_it_and_never_from_the_database_it_writes_to()
    {
        await AddProd();

        var into = await _site.RunAsync("sync", "pull", "--from", DevDatabase, "--all", "--db", "prod");
        var same = await _site.RunAsync("sync", "pull", "--from", DevDatabase, "--all");
        var unknown = await _site.RunAsync("sync", "pull", "--from", "nowhere", "--all");
        // OPTICLI_DB names the database written to, never the source: --from is chosen as --db is.
        using var _ = _site.Variable("OPTICLI_DB", $"Server=localhost,1433;Database={DevDatabase};User Id=sa;Password=hunter2-dev");
        var named = await _site.RunAsync("sync", "pull", "--from", DevDatabase, "--all");

        AssertProdRefused(into);
        Assert.True(same.ExitCode == 1, same.ToString());
        Assert.Contains("names the database sync pull writes to", same.Error().GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(unknown.ExitCode == 2, unknown.ToString());
        Assert.True(named.ExitCode == 1, named.ToString());
        Assert.Contains("names the database sync pull writes to", named.Error().GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private async Task<Run> AddProd()
    {
        var run = await _site.RunAsync("db", "add", "prod", "--role", "prod", "--connection", Prod);
        Assert.True(run.ExitCode == 0, run.ToString());
        return run;
    }

    private async Task<string> DevId() =>
        (await _site.RunAsync("db", "list")).Json().GetProperty("data").GetProperty("choices").EnumerateArray()
            .Single(c => c.GetProperty("database").GetString() == DevDatabase).GetProperty("id").GetString()!;

    private Task<Run> RunWithProjectAsync(Action<Command, GlobalOptions> build, params string[] args) =>
        Opticli.RunCommandAsync(build, [.. args, "--project", _site.ProjectFile, "--json"]);

    private static void AssertProdRefused(Run run, string? message = null, string database = ProdDatabase)
    {
        Assert.True(run.ExitCode == 3, run.ToString());
        var error = run.Error();
        Assert.Equal("refused", error.GetProperty("code").GetString());
        Assert.Equal("prodDatabase", error.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal(database, error.GetProperty("details").GetProperty("database").GetString(), StringComparer.OrdinalIgnoreCase);
        if (message is not null)
        {
            Assert.Contains(message, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
    }

    private static IEnumerable<(string Path, Command Command)> Paths(Command command, string[] path)
    {
        foreach (var child in command.Subcommands)
        {
            string[] childPath = [.. path, child.Name];
            yield return (string.Join(' ', childPath), child);
            foreach (var nested in Paths(child, childPath))
            {
                yield return nested;
            }
        }
    }
}
