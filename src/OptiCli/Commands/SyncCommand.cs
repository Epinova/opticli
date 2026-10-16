using System.CommandLine;
using System.Globalization;
using System.Text;
using OptiCli.Cli;
using OptiCli.Core.Configuration;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Export;
using OptiCli.Core.Output;
using OptiCli.Core.Queries;
using OptiCli.Core.Sync;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

/// <summary><c>sync pull</c>: another environment's content into the local database, writing only what changed.</summary>
internal static class SyncCommand
{
    /// <summary><c>details.reason</c> when the target isn't a local database.</summary>
    public const string TargetReason = "syncTarget";

    public static Command Create(GlobalOptions options)
    {
        var sync = new Command("sync", """
            Keep the local database's content in step with another environment's (`sync pull --from <name>`). The source is only
            read, over SQL, so it may be production (`opticli db add <name> --role prod`); the local database is written through
            its running site (`opticli serve`), like every write.
            """);
        sync.Subcommands.Add(Pull(options));
        return sync;
    }

    private static Command Pull(GlobalOptions options)
    {
        var from = new Option<string>("--from") { Description = "The database to read from: its name (`opticli db add`), id or database name from `opticli db list`. Only read.", HelpName = "name|id", Required = true };
        var reference = new Argument<string[]>("ref") { Description = "The content to sync with everything below it, found in the source: one or more (" + Core.Refs.ContentRefParser.Syntax + "); or --site / --all.", Arity = ArgumentArity.ZeroOrMore };
        var site = new Option<string?>("--site") { Description = "Without a ref: the site's start page with everything below it, its assets and the global assets. With a ref: the site a path ref belongs to.", HelpName = "name|host" };
        var all = new Option<bool>("--all") { Description = "Every site's start page with everything below it, the sites' assets and the global assets." };
        var languages = new Option<string?>("--languages") { Description = "Only these language branches (comma-separated codes), besides each item's master branch, which is always written.", HelpName = "en,sv" };
        var delete = new Option<bool>("--delete") { Description = "Move content synced before that the source no longer has (or has in its recycle bin) to the local recycle bin. Without it such content is only listed (gone). Never deletes for good." };
        var overwrite = new Option<bool>("--overwrite-local") { Description = "Write the source's content over local changes made since the last sync (conflicts), and restore synced content trashed locally. The local versions stay in the history. Ask the user first." };
        var strict = new Option<bool>("--strict") { Description = "Fail (exit 5) when the source has content types or properties the local build lacks or has as another type, instead of skipping that content and dropping those values." };
        var blobs = new Option<string?>("--blobs")
        {
            Description = "Where the source's media files are: a folder holding the blob containers (a copy of its App_Data/blobs), or the Azure container's https:// URL with a SAS token. Default for a local source: the project's file blob folder.",
            HelpName = "dir|url",
        };
        var output = new Option<string?>("--out") { Description = "Keep the plan the sync applies (plan.json, files/, texts/) in this folder, new or empty. Default: a temporary folder, removed afterwards.", HelpName = "dir" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Compare and validate the plan against the local site without saving anything; the sync state stays as it was." };

        var command = new Command("pull", """
            Bring a source database's content into the local development database: what the local one lacks is created, what
            differs updated (a new version, published when the source's is), what the source moved moved, what is in the local
            recycle bin restored, and with --delete what the source deleted since the last sync moved to the recycle bin. Content
            only in the local database, never synced, is left alone. Content is matched by GUID.
            It is `export` of the source (read over SQL only: production is fine), a diff against the local database, and
            `apply --update-existing` of only what changed, through the local site: `opticli serve` first. The local database must
            have the role local (refused for dev, test and prod, exit 3). Its site must run on the current build: its database
            holds the build's content model, which the source's is compared with. Content of a type the build lacks is skipped,
            properties it lacks are dropped, each listed (--strict fails instead).
            The sync state (user config folder) remembers per item what was synced, so the next pull only reads what changed:
            a local change since then, to content the source also changed, is a conflict (listed, skipped; --overwrite-local
            writes over it). The first pull of a database takes the baseline: everything is compared, what differs is updated.
            Not synced: access rights, history, authors, Forms submissions, users, the schema (a bacpac copies those).
            Example: opticli sync pull --from prod --all --blobs ../prod-blobs --dry-run
            """);
        command.Options.Add(from);
        command.Arguments.Add(reference);
        command.Options.Add(site);
        command.Options.Add(all);
        command.Options.Add(languages);
        command.Options.Add(delete);
        command.Options.Add(overwrite);
        command.Options.Add(strict);
        command.Options.Add(blobs);
        command.Options.Add(output);
        command.Options.Add(dryRun);
        WriteCommands.Mark(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var given = parse.GetValue(reference) is { Length: > 0 } refs ? refs : null;
            var siteName = parse.GetValue(site);
            var everything = parse.GetValue(all);
            if ((given is null ? 0 : 1) + (given is null && siteName is not null ? 1 : 0) + (everything ? 1 : 0) != 1)
            {
                throw new UsageException("Give refs, --site <name> or --all (one of them).", "Example: opticli sync pull --from prod /en/about-us/");
            }
            var current = context.Environment.CurrentDirectory;
            var project = context.Project;

            // The target: the development database (or --db/--connection), which must be a local one.
            var resolution = context.ResolveConnection();
            if (resolution.Chosen is { } chosen && chosen.Role != DatabaseRole.Local)
            {
                throw new RefusedException(
                    $"sync pull writes only to a local database, and '{chosen.Database}' on '{chosen.Server}' has the role {DatabaseRoles.Name(chosen.Role ?? DatabaseRole.Dev)}{(chosen.Role is null ? " (none: a remote database)" : "")}.",
                    "Pull into the local development database. A shared dev or test database is other people's; content goes there with `opticli apply`, and to production only through a sealed apply.")
                {
                    Details = new { reason = TargetReason, chosen.Server, chosen.Database, role = chosen.Role },
                };
            }

            // The source: named like --db, never by --connection or OPTICLI_DB (those name the target).
            var sourceResolution = ConnectionResolver.Resolve(context.ConnectionRequest with { Explicit = null, Profile = null, Database = parse.GetValue(from) }, project, context.Environment, runConnection: false);
            var sourceConnection = sourceResolution.Require();
            var sourceChosen = sourceResolution.Chosen!;
            if (resolution.Chosen is { Target: { } targetKey } && sourceChosen.Target is { } sameKey && targetKey == sameKey)
            {
                throw new UsageException($"--from names the database sync pull writes to ('{sourceChosen.Database}' on '{sourceChosen.Server}').", "Give the other environment's database: `opticli db list` shows them.");
            }
            using var blobSource = parse.GetValue(blobs) is { } blobsValue ? BlobSource.Parse(blobsValue, current) : null;

            await using var targetSession = await context.OpenContentAsync(cancellationToken);
            var writes = context.Writes(targetSession, updateExisting: true, ask: false);
            // Before reading anything: the site must run on this database, and its database holds the build's model.
            await context.ConnectAgentAsync(cancellationToken);
            var targetChosen = context.ResolveConnection().Chosen!;

            await using var sourceDb = await CmsDatabase.OpenAsync(sourceConnection, cancellationToken);
            await using var sourceSession = await ContentSession.OpenAsync(sourceDb, cancellationToken);
            var reader = new ExportReader(sourceSession);
            IReadOnlyList<ExportRoot> roots;
            if (given is not null)
            {
                var located = new List<ExportRoot>();
                foreach (var each in given)
                {
                    located.Add(reader.Root((await sourceSession.LocateAsync(each, siteName, cancellationToken)).Id, descendants: true));
                }
                roots = located.Distinct().ToList();
            }
            else
            {
                roots = (everything ? sourceSession.Model.Sites.All : [sourceSession.Model.Sites.RequireSite(siteName!)])
                    .SelectMany(reader.SiteRoots)
                    .Distinct()
                    .ToList();
            }
            var languageIds = parse.GetValue(languages) is { } codes
                ? codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(code => sourceSession.Model.RequireLanguage(code).Id).ToHashSet()
                : null;

            var warnings = sourceResolution.Warnings().ToList();
            var media = blobSource ?? LocalBlobs(context, project, sourceChosen.IsLocal == true, warnings);
            var (blobRoot, _) = BlobLocator.Root(project.Directory, context.Environment.Variables);
            var keep = parse.GetValue(output) is { } kept ? Folder(kept, current) : null;
            var folder = keep ?? Path.Combine(Path.GetTempPath(), $"opticli-sync-{Guid.NewGuid():N}");
            var state = SyncState.Load(
                SyncState.PathFor(context.Environment, project.Directory, sourceChosen.Target!, targetChosen.Target!),
                project.Directory, sourceChosen.Target!, targetChosen.Target!);
            using var progress = Progress.OnTerminal();
            try
            {
                var pull = new SyncPull(sourceSession, targetSession, writes, state, folder, media, Directory.Exists(blobRoot) ? blobRoot : null)
                {
                    Progress = progress.Report,
                    KeepPlan = keep is not null,
                };
                var result = await pull.RunAsync(
                    roots,
                    languageIds,
                    new SyncOptions(parse.GetValue(delete), parse.GetValue(overwrite), parse.GetValue(strict), parse.GetValue(dryRun)),
                    Database(sourceChosen),
                    Database(targetChosen),
                    cancellationToken);
                progress.Done();
                warnings.AddRange(Warnings(result));
                return new CommandResult(result, Text: Text(result), Warnings: warnings, Source: result.Run is null ? CommandResult.DbSource : WriteExecutor.AgentSource);
            }
            finally
            {
                progress.Done();
                if (keep is null && Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        });
        return command;
    }

    private static SyncDatabase Database(ConnectionCandidate candidate) =>
        new(candidate.Server, candidate.Database, candidate.Role is { } role ? DatabaseRoles.Name(role) : null);

    /// <summary><c>--out</c>: a new or empty folder (the plan's files are written there and kept).</summary>
    internal static string Folder(string output, string current)
    {
        var folder = Path.GetFullPath(output, current);
        if (File.Exists(folder) || (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()))
        {
            throw new UsageException($"{folder} isn't a new or empty folder.", "Give --out a new folder: the sync writes its plan there and keeps it.");
        }
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A local source's media: the project's file blob folder (appsettings, or the environment), when there is one.</summary>
    private static BlobSource? LocalBlobs(CliContext context, Core.Discovery.ProjectInfo project, bool local, List<string> warnings)
    {
        if (!local)
        {
            warnings.Add("The source database isn't local, so its media files aren't on this machine: new and changed media are skipped unless --blobs says where the files are.");
            return null;
        }
        var (root, _) = BlobLocator.Root(project.Directory, context.Environment.Variables);
        return Directory.Exists(root) ? BlobSource.Local(root) : null;
    }

    private static IEnumerable<string> Warnings(SyncResult result)
    {
        if (result.Conflicts.Count > 0)
        {
            yield return $"{result.Conflicts.Count} item(s) changed both in the source and locally since the last sync (conflicts) were left as they are; --overwrite-local writes the source's over them (ask the user first).";
        }
        if (result.Gone.Count > 0)
        {
            yield return $"{result.Gone.Count} item(s) synced before are gone from the source (gone); --delete moves them to the local recycle bin.";
        }
        if (result.Skipped.Count > 0)
        {
            yield return $"{result.Skipped.Count} item(s) were skipped (skipped).";
        }
        if (result.Dropped.Count > 0)
        {
            yield return $"{result.Dropped.Count} value(s) couldn't be written to the local database and were left out (dropped).";
        }
        if (result.Model.Count > 0)
        {
            yield return $"The source's content model differs from the local build's in {result.Model.Count} way(s) (model): that content or those values were left out.";
        }
        if (result.Baseline && !result.DryRun)
        {
            yield return "The first sync from this database: every item was compared, and the sync state taken (the baseline).";
        }
    }

    private static string Text(SyncResult result)
    {
        var counts = result.Counts;
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"{(result.DryRun ? "Dry run: would sync" : "Synced")} {result.Source.Database} into {result.Target.Database}: {result.Steps} step(s){(result.Run is null ? ", nothing to apply" : "")}")
            .AppendLine(CultureInfo.InvariantCulture, $"  created {counts.Created}, updated {counts.Updated}, moved {counts.Moved}, restored {counts.Restored}, trashed {counts.Trashed}, unchanged {counts.Unchanged}")
            .AppendLine(CultureInfo.InvariantCulture, $"  conflicts {counts.Conflicts}, kept local {counts.Kept}, gone {counts.Gone}, skipped {counts.Skipped}; {result.Files.Count} file(s)");
        void List(string label, IReadOnlyList<SyncEntry> entries)
        {
            foreach (var entry in entries)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {label} {entry.Ref ?? entry.Guid.ToString("D")} {entry.Name}{(entry.Reason is null ? "" : $": {entry.Reason}")}");
            }
        }
        List("created", result.Created);
        List("updated", result.Updated);
        List("moved", result.Moved);
        List("restored", result.Restored);
        List("trashed", result.Trashed);
        List("conflict", result.Conflicts);
        List("kept", result.Kept);
        List("gone", result.Gone);
        List("skipped", result.Skipped);
        foreach (var difference in result.Model)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  model {difference.Type}{(difference.Property is null ? "" : $".{difference.Property}")}: {difference.Difference}");
        }
        foreach (var dropped in result.Dropped)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  dropped {dropped.Guid} {dropped.Property}{(dropped.Language is { } language ? $" ({language})" : "")}: {dropped.Reason}");
        }
        return text.ToString();
    }

    /// <summary>Progress on stderr while the sync runs, only when stderr is a terminal (an agent reads the envelope).</summary>
    private sealed class Progress : IDisposable
    {
        private readonly bool _shown;
        private string? _phase;
        private long _last;
        private bool _open;

        private Progress(bool shown) => _shown = shown;

        public static Progress OnTerminal() => new(!Console.IsErrorRedirected);

        public void Report(string phase, int done, int total)
        {
            if (!_shown)
            {
                return;
            }
            var now = Environment.TickCount64;
            // At most a few times a second, and always the last of a phase.
            if (phase == _phase && done < total && now - _last < 200)
            {
                return;
            }
            _last = now;
            _phase = phase;
            Console.Error.Write($"\r{phase} {done}/{total}\u001b[K");
            _open = true;
        }

        public void Done()
        {
            if (_open)
            {
                Console.Error.Write("\r\u001b[K");
                _open = false;
            }
        }

        public void Dispose() => Done();
    }
}
