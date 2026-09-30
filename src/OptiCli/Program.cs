using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Commands;
using OptiCli.Core.Errors;
using OptiCli.Core.Refs;
using OptiCli.Core.Text;

var options = new GlobalOptions();
var root = new RootCommand($$$"""
    Inspect and change a local Optimizely CMS 12 (EPiServer) site from its repository: content, content types, URLs.
    Run it anywhere in the site's repository: the CMS project and its development database are found automatically
    (`opticli doctor` shows what was found). A local database is used as is; a remote one (e.g. a dev database in Azure)
    only once the user has chosen it with `opticli db use`: until then commands fail with needs_selection (exit 6).

    Refs: {{{ContentRefParser.Syntax}}}
    Reads query the database directly and need nothing running.
    Writes (set, create, area, block, translate, publish, move, delete, access, apply) go through the CMS inside the running site:
      `opticli serve` first, `opticli serve --stop` when done. They save drafts unless --publish, delete only moves to the
      recycle bin, and every write takes --dry-run.
    Output: compact JSON {"ok": true, "data": ..., "meta": {"source", "version", "next", "warnings", "database"}} when
      stdout is redirected, tables on a terminal; --json / --text force one, --jsonl (list commands) prints one item per
      line. Lists return 50 items: pass meta.next as --cursor for more, or raise --limit.
    Errors: {"ok": false, "error": {"code", "message", "hint"}}; the hint says what to do next.
    Exit codes: 0 ok, 1 usage, 2 not found, 3 refused by a safety rule, 4 database/site unreachable, 5 write conflict or
      validation failure, 6 the user must choose the development database first (error.details lists the choices).
    Details and an example for each command: opticli <command> --help
    """);
options.AddTo(root);
SummaryHelpAction.Install(root);
root.Subcommands.Add(DoctorCommand.Create(options));
root.Subcommands.Add(DbCommand.Create(options));
root.Subcommands.Add(SitesCommand.Create(options));
root.Subcommands.Add(LanguagesCommand.Create(options));
root.Subcommands.Add(TypesCommand.Create(options));
root.Subcommands.Add(TypeCommand.Create(options));
root.Subcommands.Add(GetCommand.Create(options));
root.Subcommands.Add(ResolveCommand.Create(options));
root.Subcommands.Add(UrlCommand.Create(options));
root.Subcommands.Add(TreeCommand.Create(options));
root.Subcommands.Add(ChildrenCommand.Create(options));
root.Subcommands.Add(AncestorsCommand.Create(options));
root.Subcommands.Add(FindCommand.Create(options));
root.Subcommands.Add(SearchCommand.Create(options));
root.Subcommands.Add(WhereUsedCommand.Create(options));
root.Subcommands.Add(AllowedInCommand.Create(options));
root.Subcommands.Add(VersionsCommand.Create(options));
root.Subcommands.Add(DraftsCommand.Create(options));
root.Subcommands.Add(BlobCommand.Create(options));
root.Subcommands.Add(SqlCommand.Create(options));
root.Subcommands.Add(ServeCommand.Create(options));
root.Subcommands.Add(EnvCommand.Create(options));
root.Subcommands.Add(SetCommand.Create(options));
root.Subcommands.Add(CreateCommand.Create(options));
root.Subcommands.Add(AreaCommand.Create(options));
root.Subcommands.Add(BlockCommand.Create(options));
root.Subcommands.Add(TranslateCommand.Create(options));
root.Subcommands.Add(PublishCommand.Create(options));
root.Subcommands.Add(MoveCommand.Create(options));
root.Subcommands.Add(DeleteCommand.Create(options));
root.Subcommands.Add(AccessCommand.Create(options));
root.Subcommands.Add(ApplyCommand.Create(options));
root.Subcommands.Add(SkillCommand.Create(options));

// A bare `opticli` shows the overview instead of a "required command" error.
var parse = root.Parse(args.Length == 0 ? ["--help"] : args);
if (parse.Errors.Count > 0)
{
    // Report parse errors in the same envelope as everything else, instead of System.CommandLine's help dump.
    var writer = CommandRunner.CreateWriter(parse, options, out _);
    var command = parse.CommandResult.Command;
    var path = new List<string>();
    for (var result = parse.CommandResult; result.Command != root; result = (System.CommandLine.Parsing.CommandResult)result.Parent!)
    {
        path.Insert(0, result.Command.Name);
    }
    var usage = string.Join(" ", ["opticli", .. path]);
    var typo = parse.UnmatchedTokens.Count > 0 && command.Subcommands.Count > 0
        ? Suggestions.DidYouMean(parse.UnmatchedTokens[0], command.Subcommands.Select(c => c.Name))
        : null;
    if (parse.UnmatchedTokens.Contains(options.JsonLines.Name))
    {
        return writer.Failure(ErrorCode.Usage, $"--jsonl only applies to commands that return a list, not to `{usage}`.", "Use --json for this command.");
    }
    var message = string.Join(" ", parse.Errors.Select(e => string.Join(" ", e.Message.Split((char[])['\n', '\t'], StringSplitOptions.RemoveEmptyEntries))));
    return writer.Failure(ErrorCode.Usage, message, $"{(typo is null ? "" : typo + " ")}Run `{usage} --help` for usage.");
}

return await parse.InvokeAsync();
