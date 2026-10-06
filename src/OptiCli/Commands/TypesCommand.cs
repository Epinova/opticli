using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Errors;
using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Commands;

internal static class TypesCommand
{
    private sealed record TypeSummary(string Name, ContentKind Kind, int Instances, string? DisplayName, Guid Guid)
    {
        /// <summary>With --orphaned: the class the CMS has on record, which is gone.</summary>
        public string? ModelType { get; init; }
    }

    public static Command Create(GlobalOptions options)
    {
        var kind = new Option<string?>("--kind") { Description = "Only types of this kind.", HelpName = "page|block|media|folder|other" };
        kind.AcceptOnlyFromAmong(Enum.GetNames<ContentKind>().Select(n => n.ToLowerInvariant()).ToArray());
        var unused = new Option<bool>("--unused") { Description = "Only types with no (non-deleted) content items." };
        var orphaned = new Option<bool>("--orphaned")
        {
            Description = "Only types whose class is gone from the code, which the CMS keeps because content (or a property) still uses them. Asks the running site when `opticli serve` runs (every class, packages' too); otherwise scans the site's sources for the types of its own assemblies.",
        };
        var sort = new Option<string>("--sort") { Description = "name, or instances (most used first).", DefaultValueFactory = _ => "name", HelpName = "name|instances" };
        sort.AcceptOnlyFromAmong("name", "instances");
        var list = new ListOptions(options);

        var command = new Command("types", """
            List content types with how many (non-deleted) content items use each, by name (--sort instances: most used first).
            Kinds: page, block, media (incl. images and video), folder, other (settings, system types).
            --unused: types no content uses. --orphaned: types defined in code (the CMS has a class on record) whose class is
            gone, with modelType; the CMS keeps such a type while content uses it. With `opticli serve` running the site says
            which classes it can't load; without it the site's sources are scanned (by GUID, then name) for the types of its
            own assemblies only (a warning says so).
            Example: opticli types --kind block --sort instances --limit 10
            Example: opticli types --orphaned
            """);
        command.Options.Add(kind);
        command.Options.Add(unused);
        command.Options.Add(orphaned);
        command.Options.Add(sort);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            IEnumerable<ContentTypeInfo> types = await ContentTypeReader.ListAsync(db, cancellationToken, countInstances: true);

            if (context.Parse.GetValue(kind) is { } wanted)
            {
                var parsed = Enum.Parse<ContentKind>(wanted, ignoreCase: true);
                types = types.Where(t => t.Kind == parsed);
            }
            if (context.Parse.GetValue(unused))
            {
                types = types.Where(t => t.Instances == 0);
            }
            var warnings = new List<string>();
            if (context.Parse.GetValue(orphaned))
            {
                var (found, warning) = await OrphansAsync(context, types.ToList(), cancellationToken);
                types = found;
                if (warning is not null)
                {
                    warnings.Add(warning);
                }
            }

            if (context.Parse.GetValue(sort) == "instances")
            {
                types = types.OrderByDescending(t => t.Instances).ThenBy(t => t.Name, StringComparer.Ordinal);
            }
            var showModel = context.Parse.GetValue(orphaned);
            var summaries = types.Select(t => new TypeSummary(t.Name, t.Kind, t.Instances ?? 0, t.DisplayName, t.Guid) { ModelType = showModel ? t.ModelType : null }).ToList();
            var page = list.Apply(context.Parse, summaries);
            return new CommandResult(page.Items, page.Next, Warnings: warnings.Count > 0 ? warnings : null);
        });
        return command;
    }

    /// <summary>
    /// Through the running site when <c>serve</c> runs (it knows every class it can load, packages' too); otherwise by the
    /// source scan, for the types of the solution's own assemblies only.
    /// </summary>
    /// <returns>The orphaned types, and a warning when the answer is the source scan's.</returns>
    private static async Task<(IReadOnlyList<ContentTypeInfo> Types, string? Warning)> OrphansAsync(CliContext context, IReadOnlyList<ContentTypeInfo> types, CancellationToken cancellationToken)
    {
        var why = "`opticli serve` isn't running";
        try
        {
            var agent = await context.ConnectAgentAsync(cancellationToken);
            var site = await agent.SendAsync<TypesWithoutCodeResult>(HttpMethod.Get, AgentRoutes.TypesWithoutCode, null, cancellationToken);
            return (OrphanedTypes.FromSite(types, site), null);
        }
        catch (NotFoundException ex) when (ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        {
            why = "the running site's agent is older than this opticli";
        }
        catch (OptiCliException)
        {
            // No site running: the sources it is.
        }
        var project = context.TryGetProject(out var error) ?? throw error!;
        var index = CSharpSourceIndex.Build(project.SourceRoot);
        return (OrphanedTypes.FromSource(types, index, ScheduledJobSources.Assemblies(project.SourceRoot)),
            $"Checked against the site's sources ({why}): only types of the solution's own assemblies whose class the scan doesn't find, by GUID or name. A type of a package that was removed isn't found this way; with `opticli serve` running this opticli, the site checks every type's class.");
    }
}
