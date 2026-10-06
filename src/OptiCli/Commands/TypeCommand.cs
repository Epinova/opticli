using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Discovery;
using OptiCli.Core.SourceScan;

namespace OptiCli.Commands;

/// <summary>
/// One content type: its properties from the DB, merged with what the C# declarations say (CMS 12
/// keeps tab, order and required in code unless an admin overrides them), plus class files and views.
/// </summary>
internal static class TypeCommand
{
    private sealed record TypeDetails(
        string Name,
        Guid Guid,
        ContentKind Kind,
        string? Base,
        string? DisplayName,
        string? Description,
        string? ModelType,
        int Instances,
        IReadOnlyList<PropertyDetails> Properties,
        string? SourceRoot,
        IReadOnlyList<ClassFile>? Classes,
        IReadOnlyList<ViewFile>? Views,
        string? SourceNote);

    /// <param name="Source">Where the property is declared in code (<c>path:line</c>, relative to sourceRoot).</param>
    /// <param name="DeclaredIn">The base class declaring it, when that is not the type's own class.</param>
    /// <param name="ExistsOnModel">Only present (false) for properties that exist in the DB but not in code.</param>
    /// <param name="Values">For those: how many values are stored, on content and in versions.</param>
    /// <param name="AllowedTypes">From <c>[AllowedTypes]</c> in code; absent means any type (of the right kind) is accepted.</param>
    /// <param name="RestrictedTypes">Types <c>[AllowedTypes]</c> refuses even though they match <paramref name="AllowedTypes"/>.</param>
    private sealed record PropertyDetails(
        string Name,
        string? Type,
        string? BlockType,
        bool? List,
        bool CultureSpecific,
        bool Required,
        string? Tab,
        int? Order,
        string? DisplayName,
        string? Source,
        string? DeclaredIn,
        bool? ExistsOnModel,
        IReadOnlyList<string>? AllowedTypes,
        IReadOnlyList<string>? RestrictedTypes,
        string? UiHint,
        StoredValues? Values = null);

    /// <param name="Content">Content items with a value (in any language, the recycle bin included).</param>
    /// <param name="Versions">Versions with a value. Both count values inside a block property and category selections too: what removing it deletes.</param>
    private sealed record StoredValues(int Content, int Versions);

    /// <param name="BaseTypes">The base class and interfaces as declared, e.g. to check a type against another type's allowedTypes.</param>
    private sealed record ClassFile(string File, int Line, string MatchedBy, IReadOnlyList<string> BaseTypes);

    private sealed record ViewFile(string File, string MatchedBy);

    public static Command Create(GlobalOptions options)
    {
        var name = new Argument<string>("name") { Description = "Content type name, class name or GUID." };
        var command = new Command("type", """
            Show one content type: its properties, the C# class that defines it and the Razor views that render it.
            Per property: name, type, block type, culture-specific, required, tab, order, the file:line declaring it, and
            allowedTypes/restrictedTypes from its [AllowedTypes] attribute (absent: any type) and its [UIHint] (an editor
            descriptor for that hint may change the allowed types at runtime). Tab, order and required come from the DB when
            an admin has overridden them, else from the C# attributes. classes[].baseTypes lists what the class derives from.
            Views are matched by name and @model, so teaser/partial views appear next to the page template; a controller
            (ContentController/PageController<T>) or view component is not listed: search the code for the class name.
            Reverse question (which properties accept a type): opticli allowed-in <type>.
            A property in the database but not in code has existsOnModel: false and values (how many content items and versions
            hold a value): the CMS keeps a property removed from code while it has values. One added in admin mode looks the
            same. `opticli types --orphaned` lists whole types whose class is gone; `opticli types remove-property` removes such
            a property.
            Example: opticli type ArticlePage
            """);
        command.Arguments.Add(name);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var types = await ContentTypeReader.ListAsync(db, cancellationToken, countInstances: true);
            var type = ContentTypeLookup.Find(types, context.Parse.GetValue(name)!);
            var properties = await ContentTypeReader.ListPropertiesAsync(db, type.Id, cancellationToken);
            var orphanValues = await ContentTypeReader.OrphanValuesAsync(db, properties, cancellationToken);

            var project = context.TryGetProject(out var projectError);
            var details = project is null
                ? Describe(type, properties, orphanValues, null, $"C# sources not scanned: {projectError?.Message}")
                : Describe(type, properties, orphanValues, project, null);
            var notInCode = details.Properties.Where(p => p.ExistsOnModel == false).Select(p => p.Name).ToList();
            return new CommandResult(details, Warnings: notInCode.Count == 0 ? null :
            [
                $"In the database but not in the type's code (existsOnModel: false): {string.Join(", ", notInCode)}. The CMS keeps a property removed from code while it has values (values), and properties added in admin mode look the same. values counts the CMS's own tables: what a content provider (a catalog, a DAM) keeps for it isn't seen here; `opticli types remove-property <type> <property> --dry-run` asks the site, which asks the providers.",
            ]);
        });
        return command;
    }

    private static TypeDetails Describe(ContentTypeInfo type, IReadOnlyList<PropertyDefinitionInfo> properties, IReadOnlyDictionary<int, (int Content, int Versions)> orphanValues,
        ProjectInfo? project, string? sourceNote)
    {
        IReadOnlyList<ClassMatch> classes = [];
        IReadOnlyDictionary<string, PropertySource> code = new Dictionary<string, PropertySource>();
        IReadOnlyList<ViewMatch> views = [];
        var root = project?.SourceRoot;

        if (root is not null)
        {
            var index = CSharpSourceIndex.Build(root);
            var className = type.ClassName ?? type.Name;
            classes = ContentTypeSources.FindClasses(index, type.Guid, className, type.Namespace);
            if (classes.Count > 0)
            {
                code = ContentTypeSources.FindProperties(index, classes[0].Class);
            }
            var names = new HashSet<string>(StringComparer.Ordinal) { type.Name, className };
            foreach (var match in classes)
            {
                names.Add(match.Class.Name);
            }
            views = ContentTypeSources.FindViews(root, names);
        }

        var mainClass = classes.FirstOrDefault()?.Class.Name;
        var details = properties
            .Select(p =>
            {
                var source = code.GetValueOrDefault(p.Name);
                return new PropertyDetails(
                    p.Name,
                    p.DataType,
                    p.BlockType,
                    p.IsList ? true : null,
                    p.CultureSpecific,
                    p.Required ?? source?.Required ?? false,
                    p.Tab ?? source?.Tab,
                    p.FieldOrder ?? source?.Order,
                    p.EditCaption ?? source?.DisplayName,
                    source is null ? null : $"{Relative(root!, source.File)}:{source.Line}",
                    source is not null && source.DeclaredIn != mainClass ? source.DeclaredIn : null,
                    p.ExistsOnModel ? null : false,
                    source?.AllowedTypes is { Allowed.Count: > 0 } allowed ? allowed.Allowed : null,
                    source?.AllowedTypes is { Restricted.Count: > 0 } restricted ? restricted.Restricted : null,
                    source?.UiHint,
                    orphanValues.TryGetValue(p.Id, out var values) ? new StoredValues(values.Content, values.Versions) : null);
            })
            .OrderBy(p => p.Order ?? int.MaxValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TypeDetails(
            type.Name,
            type.Guid,
            type.Kind,
            type.Base,
            type.DisplayName,
            type.Description,
            type.ModelType,
            type.Instances ?? 0,
            details,
            root,
            root is null ? null : classes.Select(c => new ClassFile(Relative(root, c.Class.File), c.Class.Line, c.MatchedBy, c.Class.BaseTypes)).ToList(),
            root is null ? null : views.Select(v => new ViewFile(Relative(root, v.File), v.MatchedBy)).ToList(),
            sourceNote);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);
}
