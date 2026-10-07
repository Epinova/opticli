using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Configuration;
using OptiCli.Core.Drift;
using OptiCli.Core.Serve;
using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Commands;

internal static class TypesCommand
{
    private sealed record TypeSummary(string Name, ContentKind Kind, int Instances, string? DisplayName, Guid Guid)
    {
        /// <summary>With --orphaned: the class the CMS has on record, which is gone.</summary>
        public string? ModelType { get; init; }

        /// <summary>With --orphaned, CMS 13: no class or model-sync version on record (made in admin mode, or a code type a content import overwrote).</summary>
        public bool? OriginUnknown { get; init; }

        /// <summary>CMS 13: the type's Visual Builder blueprints, which <see cref="Instances"/> doesn't count.</summary>
        public int? Blueprints { get; init; }

        /// <summary>CMS 13: <c>SectionEnabled</c> (it can stand in an experience's outline), <c>ElementEnabled</c> (in a section's columns).</summary>
        public IReadOnlyList<string>? CompositionBehaviors { get; init; }

        /// <summary>CMS 13: the contracts (kind contract) the type implements.</summary>
        public IReadOnlyList<string>? Contracts { get; init; }

        /// <summary>CMS 13: the external content source the type belongs to; its content isn't in this database.</summary>
        public string? Source { get; init; }
    }

    public static Command Create(GlobalOptions options)
    {
        var kind = new Option<string?>("--kind") { Description = "Only types of this kind: page, block, media, folder or other; on CMS 13 also experience, section, element or contract.", HelpName = "kind" };
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
            Kinds: page, block, media (incl. images and video), folder, other (settings, system types); on CMS 13 also
            Visual Builder's experience (a page made of sections), section, element (a block type sections can hold) and
            contract (an interface other types implement; contracts lists them on each type), with compositionBehaviors and
            blueprints (how many Visual Builder blueprints of the type there are; instances doesn't count them).
            --unused: types no content uses. --orphaned: types defined in code (the CMS has a class on record) whose class is
            gone, with modelType; the CMS keeps such a type while content uses it. With `opticli serve` running the site says
            which classes it can't load; without it the site's sources are scanned (by GUID, then name) for the types of its
            own assemblies only (a warning says so).
            `types remove`, `types remove-property` and `types prune` remove such orphans through the site (needs `opticli serve`).
            Example: opticli types --kind block --sort instances --limit 10
            Example: opticli types --orphaned
            """);
        command.Options.Add(kind);
        command.Options.Add(unused);
        command.Options.Add(orphaned);
        command.Options.Add(sort);
        list.AddTo(command);
        foreach (var subcommand in TypesRemoveCommand.Create(options))
        {
            command.Subcommands.Add(subcommand);
        }

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
            var askedSite = false;
            if (context.Parse.GetValue(orphaned))
            {
                var (found, warning, fromSite) = await OrphansAsync(context, db, types.ToList(), cancellationToken);
                types = found;
                askedSite = fromSite;
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
            var summaries = types.Select(t => new TypeSummary(t.Name, t.Kind, t.Instances ?? 0, t.DisplayName, t.Guid)
            {
                ModelType = showModel ? t.ModelType : null,
                OriginUnknown = showModel ? t.OriginUnknown : null,
                Blueprints = t.Blueprints,
                CompositionBehaviors = t.CompositionBehaviors.Count > 0 ? t.CompositionBehaviors : null,
                Contracts = t.Contracts.Count > 0 ? t.Contracts : null,
                Source = t.Source,
            }).ToList();
            var page = list.Apply(context.Parse, summaries);
            return new CommandResult(page.Items, page.Next, Warnings: warnings.Count > 0 ? warnings : null,
                Source: askedSite ? Core.Writes.WriteExecutor.AgentSource : CommandResult.DbSource);
        });
        return command;
    }

    /// <summary>
    /// Through the running site when <c>serve</c> runs (it knows every class it can load, packages' too); otherwise by the
    /// source scan, for the types of the solution's own assemblies only.
    /// </summary>
    /// <returns>The orphaned types, a warning when the answer is the source scan's (saying why), and whether the site answered.</returns>
    private static async Task<(IReadOnlyList<ContentTypeInfo> Types, string? Warning, bool FromSite)> OrphansAsync(CliContext context, Core.Data.CmsDatabase db, IReadOnlyList<ContentTypeInfo> types, CancellationToken cancellationToken)
    {
        var answer = await SiteFallback.AskAsync(context.ConnectAgentAsync,
            agent => agent.SendAsync<TypesWithoutCodeResult>(HttpMethod.Get, AgentRoutes.TypesWithoutCode, null, cancellationToken), cancellationToken);
        if (answer.Value is { } site)
        {
            return (OrphanedTypes.FromSite(types, site), null, true);
        }
        var project = context.TryGetProject(out var error) ?? throw error!;
        var index = CSharpSourceIndex.Build(project.SourceRoot);
        var found = OrphanedTypes.FromSource(types, index, ScheduledJobSources.Assemblies(project.SourceRoot));
        var warning = $"Checked against the site's sources ({answer.WhyNot}): only types of the solution's own assemblies whose class the scan doesn't find, by GUID or name. A type of a package that was removed isn't found this way; with `opticli serve` running this opticli, the site checks every type's class.";
        var schema = await db.SchemaAsync(cancellationToken);
        if (!schema.Compositions)
        {
            return (found, warning, false);
        }
        // CMS 13 records no class for a class with a GUID: those types are checked against the GUIDs of the build's classes.
        string? output;
        try
        {
            var settings = UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory);
            output = Path.GetDirectoryName(OutputLocator.Locate(project, null, settings?.Output, context.Environment.CurrentDirectory).Dll);
        }
        catch (Core.Errors.NotFoundException)
        {
            output = null;
        }
        if (output is null)
        {
            return (found, $"{warning} CMS 13 records no class for a type whose class has a GUID, and the site has no build output to look for those GUIDs in: build it (dotnet build) to check them too.", false);
        }
        var withoutClass = OrphanedTypes.WithoutClassOnRecord(types, BuildScanner.ContentTypeGuids(output), index).ToDictionary(t => t.Id);
        var listed = found.Select(t => t.Id).ToHashSet();
        return (types.Where(t => listed.Contains(t.Id) || withoutClass.ContainsKey(t.Id)).Select(t => withoutClass.GetValueOrDefault(t.Id) ?? t).ToList(),
            $"{warning} CMS 13 records no class for a type whose class has a GUID: such types are listed when no class in the build output ({Path.GetRelativePath(project.Directory, output)}) has their GUID; originUnknown marks those without a model-sync version either (made in admin mode, or code types a content import overwrote).", false);
    }
}
