using System.CommandLine;
using OptiCli.Core.Configuration;

namespace OptiCli.Cli;

/// <summary>Options accepted by every command.</summary>
internal sealed class GlobalOptions
{
    public Option<string?> Project { get; } = new("--project")
    {
        Description = "The site's .csproj, or a directory/solution to search. Default: found from the working directory.",
        HelpName = "path",
        Recursive = true,
    };

    public Option<string?> Connection { get; } = new("--connection")
    {
        Description = "Connection string to use for this run instead of the project's configuration (also: OPTICLI_DB). A remote one that isn't the development database is flagged in meta.warnings.",
        HelpName = "string",
        Recursive = true,
    };

    public Option<string> ConnectionName { get; } = new("--connection-name")
    {
        Description = "Name of the connection string in the site's configuration.",
        DefaultValueFactory = _ => ConnectionRequest.DefaultName,
        HelpName = "name",
        Recursive = true,
    };

    public Option<string?> Database { get; } = new("--db")
    {
        Description = "Use another of the project's connection strings for this run: its id or database name from `opticli db list`. Default: the development database.",
        HelpName = "id|name",
        Recursive = true,
    };

    public Option<string?> Profile { get; } = new("--profile")
    {
        Description = "Launch profile in Properties/launchSettings.json whose connection string to use.",
        HelpName = "name",
        Recursive = true,
    };

    public Option<bool> Json { get; } = new("--json")
    {
        Description = "Force JSON output (the default when stdout is redirected).",
        Recursive = true,
    };

    public Option<bool> Text { get; } = new("--text")
    {
        Description = "Force human-readable tables (the default on a terminal).",
        Recursive = true,
    };

    /// <summary>Only on commands whose data is a list (see <see cref="AddJsonLines"/>), so help never offers it where it means nothing.</summary>
    public Option<bool> JsonLines { get; } = new("--jsonl")
    {
        Description = """One JSON object per line: each item, then {"meta":{...}} last only if there is a next page or warnings. Errors: the usual one-line error envelope.""",
    };

    public void AddTo(RootCommand root)
    {
        root.Options.Add(Project);
        root.Options.Add(Connection);
        root.Options.Add(ConnectionName);
        root.Options.Add(Database);
        root.Options.Add(Profile);
        root.Options.Add(Json);
        root.Options.Add(Text);
    }

    public void AddJsonLines(Command command) => command.Options.Add(JsonLines);
}
