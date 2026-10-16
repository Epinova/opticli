using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Export;
using OptiCli.Core.Output;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class ExportCommand
{
    /// <summary>What an export never writes, said in every result so nobody takes it for a copy of everything.</summary>
    private static readonly string[] NotExported =
    [
        "access rights",
        "versions other than the one written per branch (history, newer drafts, content variations), and who saved them when",
        "content in the recycle bin, the root, the recycle bin and the asset roots themselves",
        "Forms submissions, visitor groups, categories, users and everything else that isn't content",
    ];

    private sealed record ExportSource(string? Server, string? Database, string? Role, DateTime ReadAt);

    private sealed record ExternalRef(Guid Guid, string? Ref, string? Type, string? Name, bool? Missing, bool? Deleted, bool? Placement, IReadOnlyList<Guid> From);

    private sealed record VisitorGroup(string Id, string? Name);

    /// <param name="From">Where the files were read: a folder, or a container URL without its token.</param>
    private sealed record FileCount(int Count, long Bytes, string? From);

    private sealed record ExportResult(
        string Folder,
        string Plan,
        ExportSource Source,
        int Items,
        int Steps,
        IReadOnlyDictionary<string, int> Kinds,
        IReadOnlyDictionary<string, int> Types,
        IReadOnlyDictionary<string, int> Languages,
        FileCount Files,
        int Texts,
        int HeldBack,
        IReadOnlyList<SkippedItem> Skipped,
        IReadOnlyList<LeftOutDraft> Drafts,
        IReadOnlyList<ExternalRef> ExternalRefs,
        IReadOnlyList<VisitorGroup> VisitorGroups,
        IReadOnlyList<DroppedValue> Dropped,
        IReadOnlyList<string> ProviderRefs,
        IReadOnlyList<string> NotExported);

    public static Command Create(GlobalOptions options)
    {
        var reference = new Argument<string[]>("ref") { Description = "The content to export, one or more (" + Core.Refs.ContentRefParser.Syntax + "); or --site / --all.", Arity = ArgumentArity.ZeroOrMore };
        var descendants = new Option<bool>("--descendants") { Description = "Everything below each ref too." };
        var languages = new Option<string?>("--languages") { Description = "Only these language branches (comma-separated codes), besides each item's master branch, which is always written.", HelpName = "en,sv" };
        var allLanguages = new Option<bool>("--all-languages") { Description = "Every language branch (the default)." };
        var output = new Option<string?>("--out") { Description = "The folder to write (created; a folder with an earlier export's plan.json is written over). Default: export-<database>-<time> here.", HelpName = "dir" };
        var noMedia = new Option<bool>("--no-media") { Description = "Leave media out: no files are read, and media is referred to by GUID (listed in externalRefs)." };
        var blobs = new Option<string?>("--blobs")
        {
            Description = "Where media files are: a folder holding the blob containers (a copy of the site's App_Data/blobs), or the Azure container's https:// URL with a SAS token. Default for a local database: the project's file blob folder.",
            HelpName = "dir|url",
        };
        var site = new Option<string?>("--site") { Description = "Without a ref: export the site's start page with everything below it, its assets and the global assets. With a ref: the site a path ref belongs to.", HelpName = "name|host" };
        var all = new Option<bool>("--all") { Description = "Every site's start page with everything below it, the sites' assets and the global assets." };

        var command = new Command("export", """
            Write content of any database as a plan folder (plan.json, files/ for media, texts/ for long rich text) that
            `opticli apply --update-existing` brings into another database with the same GUIDs. Reads the database only
            (over SQL, never through the site), so it works on a production database (`--db <name>`).
            Every item becomes a create, block or upload step with its source GUID, its values and built-in settings (publish
            dates, URL segment, menu visibility, simple address, shortcut, sorting, category); references to what the plan
            creates are $id, to anything else its GUID (listed in externalRefs: the target must have it). Values that refer to
            content created later (a cycle) follow in a set step; other language branches are translate steps. A published
            branch is published, others stay drafts; a newer draft than the published version is left out (drafts).
            Blocks and media in a "For this page" folder go with their owner. data.notExported says what never is.
            Example: opticli export /en/about-us/ /globalassets/about-us/ --descendants --db prod --blobs ../prod-blobs --out about-us
            """);
        command.Arguments.Add(reference);
        command.Options.Add(descendants);
        command.Options.Add(languages);
        command.Options.Add(allLanguages);
        command.Options.Add(output);
        command.Options.Add(noMedia);
        command.Options.Add(blobs);
        command.Options.Add(site);
        command.Options.Add(all);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var given = parse.GetValue(reference) is { Length: > 0 } refs ? refs : null;
            var siteName = parse.GetValue(site);
            var everything = parse.GetValue(all);
            if ((given is null ? 0 : 1) + (given is null && siteName is not null ? 1 : 0) + (everything ? 1 : 0) != 1)
            {
                throw new UsageException("Give refs, --site <name> or --all (one of them).", "Example: opticli export /en/about-us/ --descendants");
            }
            if (given is null && parse.GetValue(descendants))
            {
                throw new UsageException("--descendants is for a ref; --site and --all export everything below the start pages.");
            }
            if (parse.GetValue(languages) is not null && parse.GetValue(allLanguages))
            {
                throw new UsageException("Give --languages or --all-languages, not both.");
            }
            if (parse.GetValue(noMedia) && parse.GetValue(blobs) is not null)
            {
                throw new UsageException("--no-media reads no files; leave --blobs out.");
            }
            var current = context.Environment.CurrentDirectory;
            using var blobSource = parse.GetValue(blobs) is { } blobsValue ? BlobSource.Parse(blobsValue, current) : null;

            var readAt = DateTime.UtcNow;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var chosen = context.ResolveConnection().Chosen;
            var reader = new ExportReader(session);
            IReadOnlyList<ExportRoot> roots;
            if (given is not null)
            {
                var located = new List<ExportRoot>();
                foreach (var each in given)
                {
                    located.Add(reader.Root((await session.LocateAsync(each, siteName, cancellationToken)).Id, parse.GetValue(descendants)));
                }
                roots = located.Distinct().ToList();
            }
            else
            {
                roots = (everything ? session.Model.Sites.All : [session.Model.Sites.RequireSite(siteName!)])
                    .SelectMany(reader.SiteRoots)
                    .Distinct()
                    .ToList();
            }
            var languageIds = parse.GetValue(languages) is { } codes
                ? codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(code => session.Model.RequireLanguage(code).Id).ToHashSet()
                : null;

            // Checked before reading (a folder that isn't an export's is refused at once), cleared only once the read worked.
            var folder = Folder(parse.GetValue(output), current, chosen?.Database, readAt);
            var read = await reader.ReadAsync(roots, languageIds, cancellationToken);
            Clear(folder);

            // Media: the file from the blobs, or the item is left out (its references then go by GUID).
            var skipped = read.Skipped.ToList();
            var warnings = new List<string>();
            var media = blobSource ?? (parse.GetValue(noMedia) ? null : LocalBlobs(context, chosen?.IsLocal == true, warnings));
            var items = new List<ExportItem>();
            var files = new FileCount(0, 0, media?.Description);
            foreach (var item in read.Items)
            {
                if (item.Kind != ContentKind.Media)
                {
                    items.Add(item);
                    continue;
                }
                var name = (string?)item.Branches[0].Document["name"];
                var uri = read.BlobUris.GetValueOrDefault(item.Guid);
                var problem = parse.GetValue(noMedia) ? "--no-media"
                    : uri is null ? "it has no file (no blob URI)"
                    : media is null ? "no --blobs to read its file from"
                    : null;
                if (problem is null)
                {
                    var relative = ExportPlanBuilder.FilePath(item.Guid, ExportFiles.FileName(name, uri!));
                    var destination = Path.Combine(folder, relative);
                    problem = await media!.CopyAsync(uri!, destination, cancellationToken);
                    if (problem is null)
                    {
                        items.Add(item with { File = relative });
                        files = files with { Count = files.Count + 1, Bytes = files.Bytes + new FileInfo(destination).Length };
                        continue;
                    }
                }
                skipped.Add(new SkippedItem(ContentIdentity.RefFor(item.Id), item.Guid, item.Type, name, $"media without its file: {problem}"));
            }

            var plan = ExportPlanBuilder.Build(items);
            foreach (var (path, text) in plan.Texts)
            {
                var file = Path.Combine(folder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), cancellationToken);
            }
            var planned = items.Where(i => !plan.Skipped.Any(s => s.Guid == i.Guid)).ToList();
            var source = new JsonObject
            {
                ["server"] = chosen?.Server,
                ["database"] = chosen?.Database,
                ["role"] = chosen?.Role is { } role ? JsonOutput.ToNode(role) : null,
                ["readAt"] = readAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["opticli"] = ToolInfo.Version,
                ["cms"] = session.Model.Schema.Major,
                // Phase 3 compares these with the source when it syncs again: the version written per branch.
                ["versions"] = new JsonObject(planned.Select(i => KeyValuePair.Create(i.Guid.ToString("D"),
                    (JsonNode?)new JsonObject(i.Branches.Select(b => KeyValuePair.Create(b.Language ?? "*", (JsonNode?)b.VersionId)))))),
            };
            var planPath = Path.Combine(folder, "plan.json");
            var document = new JsonObject { [Core.Writes.WritePlan.SourceField] = source, ["operations"] = plan.Operations };
            await File.WriteAllTextAsync(planPath, document.ToJsonString(PlanJson) + "\n", new UTF8Encoding(false), cancellationToken);

            await session.Identities.LoadAsync([], plan.ExternalRefs.Select(e => e.Guid), cancellationToken);
            var externals = plan.ExternalRefs.Select(e =>
            {
                var identity = session.Identities.ByGuid(e.Guid, null);
                return new ExternalRef(e.Guid, identity.Ref, identity.Type, identity.Name, identity.Missing, identity.Deleted, e.Placement ? true : null, e.From);
            }).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            skipped.AddRange(plan.Skipped.Select(s => items.First(i => i.Guid == s.Guid) is var item
                ? new SkippedItem(ContentIdentity.RefFor(item.Id), item.Guid, item.Type, (string?)item.Branches[0].Document["name"], s.Reason)
                : throw new InvalidOperationException("A skipped item the export didn't read.")));

            if (externals.Any(e => e.Missing == true || e.Deleted == true))
            {
                warnings.Add($"{externals.Count(e => e.Missing == true || e.Deleted == true)} referenced item(s) are missing or in the source's recycle bin (externalRefs): a target without them refuses those values.");
            }
            if (skipped.Count > 0)
            {
                warnings.Add($"{skipped.Count} item(s) were left out (skipped).");
            }
            if (read.Dropped.Count + plan.Dropped.Count > 0)
            {
                warnings.Add($"{read.Dropped.Count + plan.Dropped.Count} value(s) can't be written to another database and were left out (dropped).");
            }
            if (plan.ProviderRefs.Count > 0)
            {
                warnings.Add($"{plan.ProviderRefs.Count} reference(s) to a content provider's content are kept as they are (providerRefs): the target needs the same provider.");
            }
            if (read.Drafts.Count > 0)
            {
                warnings.Add($"{read.Drafts.Count} newer draft(s) than the published version were left out (drafts).");
            }

            var result = new ExportResult(
                folder,
                planPath,
                new ExportSource(chosen?.Server, chosen?.Database, chosen?.Role is { } r ? JsonOutput.ToNode(r)!.GetValue<string>() : null, readAt),
                planned.Count,
                plan.Operations.Count,
                Count(planned.Select(i => i.Kind.ToString().ToLowerInvariant())),
                Count(planned.Select(i => i.Type)),
                Count(planned.SelectMany(i => i.Branches).Select(b => b.Language ?? "*")),
                files,
                plan.Texts.Count,
                plan.HeldBack,
                skipped,
                read.Drafts,
                externals,
                plan.VisitorGroups.Select(g => new VisitorGroup(g, session.Model.VisitorGroupName(g))).ToList(),
                [.. read.Dropped, .. plan.Dropped],
                plan.ProviderRefs,
                NotExported);
            return new CommandResult(result, Text: Text(result), Warnings: warnings);
        });
        return command;
    }

    private static readonly JsonSerializerOptions PlanJson = new(JsonOutput.Options) { WriteIndented = true };

    /// <summary>
    /// The folder to write: <c>--out</c>, made if it doesn't exist. One with something in it must be an earlier export's
    /// (its <c>plan.json</c> has the <c>source</c> an export writes), which <see cref="Clear"/> then empties of what an
    /// export writes; any other folder with something in it is refused, also one with a plan of its own, whose files an
    /// export would delete.
    /// </summary>
    internal static string Folder(string? output, string current, string? database, DateTime now)
    {
        var folder = Path.GetFullPath(output ?? $"export-{ExportFiles.Safe(database ?? "database")}-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}", current);
        if (File.Exists(folder))
        {
            throw new UsageException($"{folder} is a file.", "Give --out a folder: a new one, or one an earlier export wrote.");
        }
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() && !IsExport(Path.Combine(folder, "plan.json")))
        {
            throw new UsageException($"{folder} isn't empty and isn't an earlier export's folder (a plan.json with the source export writes).",
                "Give --out a new folder, or one an earlier export wrote.");
        }
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Removes what an earlier export wrote in <paramref name="folder"/>: its plan, files and texts.</summary>
    internal static void Clear(string folder)
    {
        File.Delete(Path.Combine(folder, "plan.json"));
        foreach (var part in (string[])["files", "texts"])
        {
            if (Directory.Exists(Path.Combine(folder, part)))
            {
                Directory.Delete(Path.Combine(folder, part), recursive: true);
            }
        }
    }

    /// <summary>Whether <paramref name="plan"/> is a plan an export wrote: its <c>source</c> has the time it was read and opticli's version.</summary>
    private static bool IsExport(string plan)
    {
        try
        {
            return File.Exists(plan)
                && JsonNode.Parse(File.ReadAllText(plan)) is JsonObject { } root
                && root[Core.Writes.WritePlan.SourceField] is JsonObject { } source
                && source["readAt"] is JsonValue && source["opticli"] is JsonValue && root["operations"] is JsonArray;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A local database's media: the project's file blob folder (appsettings, or the environment), when there is one.</summary>
    private static BlobSource? LocalBlobs(CliContext context, bool local, List<string> warnings)
    {
        if (!local)
        {
            warnings.Add("The database isn't local, so its media files aren't on this machine: media is left out unless --blobs says where the files are.");
            return null;
        }
        if (context.TryGetProject(out _) is not { } project)
        {
            warnings.Add("No project found, so the file blob folder is unknown: media is left out unless --blobs says where the files are.");
            return null;
        }
        var (root, _) = BlobLocator.Root(project.Directory, context.Environment.Variables);
        return Directory.Exists(root) ? BlobSource.Local(root) : null;
    }

    private static IReadOnlyDictionary<string, int> Count(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static string Text(ExportResult result)
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Exported {result.Items} item(s) from {result.Source.Database} as {result.Steps} step(s): {result.Plan}")
            .AppendLine(CultureInfo.InvariantCulture, $"  {string.Join(", ", result.Kinds.Select(k => $"{k.Value} {k.Key}"))}; languages {string.Join(", ", result.Languages.Select(l => $"{l.Key} {l.Value}"))}; {result.Files.Count} file(s)");
        if (result.ExternalRefs.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  refers to {result.ExternalRefs.Count} item(s) it doesn't export (externalRefs): the target must have them");
        }
        foreach (var skipped in result.Skipped)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  skipped {skipped.Ref} {skipped.Name}: {skipped.Reason}");
        }
        foreach (var dropped in result.Dropped)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  dropped {dropped.Guid} {dropped.Property}{(dropped.Language is { } language ? $" ({language})" : "")}: {dropped.Reason}");
        }
        return text.ToString();
    }
}
