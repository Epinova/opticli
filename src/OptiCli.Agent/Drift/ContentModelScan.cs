using EPiServer.DataAbstraction;
using EPiServer.DataAbstraction.Migration;
using EPiServer.DataAbstraction.RuntimeModel;
using EPiServer.DataAbstraction.RuntimeModel.Internal;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Protocol;
using ContentTypeModel = EPiServer.DataAbstraction.RuntimeModel.ContentTypeModel;

namespace OptiCli.Agent.Drift;

/// <summary>
/// Compares the site's content models with the database the way the CMS's model sync does, with the sync's own
/// analysis (<see cref="ContentTypeModelRegister.AnalyzeTypes"/> and <see cref="ContentTypeModelRegister.AnalyzeProperties"/>),
/// and without its commit: nothing is written.
/// </summary>
/// <remarks>
/// <para>The register is transient, so this gets one of its own and fills it with the models the startup scan registered in
/// the CMS's content type model repository. The analysis sets each model's state and matched content type, as a sync
/// with commit would have; nothing at runtime depends on those being unset.</para>
/// <para>The analysis matches each model to its content type and property definitions (by GUID, class, name, and the old
/// names migration steps give). Its in-sync verdict isn't used: the content type repository fills the settings the
/// database leaves unset for models (display names, required, order, tab) from the running code, so only what the
/// database does store is compared (see <see cref="TypeSettings"/> and <see cref="PropertySettings"/>).</para>
/// </remarks>
internal static partial class ContentModelScan
{
    /// <summary>Content types the CMS makes itself, which <c>ListUnusedTypes</c> skips too.</summary>
    private static readonly string[] SystemTypes = ["SysRoot", "SysRecycleBin", "SysContentFolder", "SysContentAssetFolder"];

    public static (List<DriftItem> ContentTypes, List<DriftItem> Properties) Compare(IServiceProvider services)
    {
        // CMS 13 also registers a model per base type (PageData, BlockData, ..., ExperienceData, ContentFolder), for the
        // system properties those give their content types: they aren't content types of their own.
        var bases = services.GetServices<IContentTypeBaseProvider>()
            .SelectMany(p => p.ContentTypeBases.Select(p.Resolve))
            .OfType<Type>()
            .ToHashSet();
        var models = Compat.AgentBuild.ContentTypeModels(services).Where(m => m.ModelType is null || !bases.Contains(m.ModelType)).ToList();
        var register = services.GetRequiredService<ContentTypeModelRegister>();
        register.RunSynchronously = true;
        foreach (var model in models)
        {
            register.TypeModels.Add(model);
        }
        register.AnalyzeTypes();
        // The property analysis reads each model's content type, which the commit creates for new ones first.
        foreach (var model in register.TypeModels.Where(m => m.ExistingContentType is null || m.State is SynchronizationStatus.New or SynchronizationStatus.EarlierVersion).ToList())
        {
            register.TypeModels.Remove(model);
        }
        register.AnalyzeProperties();

        var renames = services.GetRequiredService<MigrationStepRepository>().Changes.ToList();
        var resolve = Compat.AgentBuild.PropertyTypeResolver(services);
        var types = services.GetRequiredService<IContentTypeRepository>();
        var matches = models.Select(m => Match(m, renames, resolve, types)).ToList();
        var matched = models.Where(m => m.State != SynchronizationStatus.New && m.ExistingContentType is not null).Select(m => m.ExistingContentType.ID).ToHashSet();
        var onlyInDatabase = types.List()
            .Where(t => Compat.AgentBuild.FromCode(t) && !SystemTypes.Contains(t.Name) && !matched.Contains(t.ID)
                && !renames.Any(c => string.Equals(c.OldName, t.Name, StringComparison.Ordinal)))
            .Select(t => t.Name);
        return ContentModelComparison.Items(matches, onlyInDatabase);
    }

    private static TypeMatch Match(ContentTypeModel model, IReadOnlyList<ContentTypeChange> renames, Func<PropertyDefinitionModel, PropertyDefinitionType?> resolve, IContentTypeRepository types)
    {
        var code = Settings(model);
        if (model.State == SynchronizationStatus.New || model.ExistingContentType is not { } existing)
        {
            return new TypeMatch(code, null, [], []);
        }
        var stored = Settings(existing);
        if (model.State == SynchronizationStatus.EarlierVersion)
        {
            return new TypeMatch(code, stored, [], [], NewerVersion: Compat.AgentBuild.SyncedVersion(existing) ?? "?");
        }
        var change = renames.FirstOrDefault(c => string.Equals(c.Name, model.Name, StringComparison.OrdinalIgnoreCase));
        var renamedFrom = change?.OldName is { Length: > 0 } old && string.Equals(old, existing.Name, StringComparison.Ordinal)
            && !string.Equals(old, model.Name, StringComparison.Ordinal) ? old : null;
        var properties = model.PropertyDefinitionModels.Select(p => Match(p, change, resolve, types)).ToList();
        // The definitions the database has. CMS 13's analysis changes the content type it matched as the commit would: it
        // adds the model's new properties (no id yet) and drops the ones the code lacks (or marks them gone), so the
        // stored type is read again.
        var definitions = (types.Load(existing.ID) ?? existing).PropertyDefinitions;
        var onlyInDatabase = definitions
            .Where(d => d.ID > 0 && d.ExistsOnModel && !model.PropertyDefinitionModels.Any(p => p.State != SynchronizationStatus.New && p.ExistingPropertyDefinition?.ID == d.ID))
            .Select(d => d.Name)
            .ToList();
        return new TypeMatch(code, stored, properties, onlyInDatabase, renamedFrom);
    }

    private static PropertyMatch Match(PropertyDefinitionModel model, ContentTypeChange? change, Func<PropertyDefinitionModel, PropertyDefinitionType?> resolve, IContentTypeRepository types)
    {
        var code = Settings(model, resolve, types);
        if (model.State == SynchronizationStatus.New || model.ExistingPropertyDefinition is not { } existing)
        {
            return new PropertyMatch(code, null);
        }
        var renamedFrom = change?.PropertyChanges.FirstOrDefault(p => string.Equals(p.Name, model.Name, StringComparison.OrdinalIgnoreCase))?.OldName is { Length: > 0 } old
            && string.Equals(old, existing.Name, StringComparison.OrdinalIgnoreCase) && !string.Equals(old, model.Name, StringComparison.OrdinalIgnoreCase) ? old : null;
        return new PropertyMatch(code, Settings(existing, types), renamedFrom);
    }

    private static TypeSettings Settings(ContentTypeModel model) => new(
        model.Name,
        WithoutVersion(model.ModelType?.AssemblyQualifiedName),
        BaseName(model.Base),
        model.Guid);

    private static TypeSettings Settings(ContentType type) => new(type.Name, WithoutVersion(type.ModelTypeString), BaseName(type.Base), type.GUID);

    /// <remarks>
    /// A CMS 13 block property's type is the one generic <c>Block</c> type, whatever its block type: there the block type's
    /// name stands for the type, and is what is compared (CMS 12 has a property type per block type, told apart by id).
    /// </remarks>
    private static PropertySettings Settings(PropertyDefinitionModel model, Func<PropertyDefinitionModel, PropertyDefinitionType?> resolve, IContentTypeRepository types)
    {
        var type = ResolveType(model, resolve);
        return Compat.AgentBuild.ModelBlockType(model, type, types) is { } block
            ? new PropertySettings(model.Name, block, null, model.CultureSpecific ?? false)
            : new PropertySettings(model.Name, TypeName(type), type?.ID, model.CultureSpecific ?? false);
    }

    private static PropertySettings Settings(PropertyDefinition definition, IContentTypeRepository types) =>
        Compat.AgentBuild.StoredBlockType(definition, types) is { } block
            ? new(definition.Name, block, null, definition.LanguageSpecific, CultureSpecificByAdmin(definition))
            : new(definition.Name, TypeName(definition.Type), definition.Type?.ID, definition.LanguageSpecific, CultureSpecificByAdmin(definition));

    /// <summary>
    /// Culture-specific set in admin mode, which wins over the code. The CMS records who set it in an internal property
    /// (<c>CultureSpecificValue</c>: <c>TrueByModel</c>, <c>FalseByOtherThanModel</c>, ...), read by name; without it,
    /// culture-specific is compared.
    /// </summary>
    internal static bool CultureSpecificByAdmin(PropertyDefinition definition) =>
        CultureSpecificValue?.GetValue(definition)?.ToString()?.EndsWith("OtherThanModel", StringComparison.Ordinal) == true;

    private static readonly System.Reflection.PropertyInfo? CultureSpecificValue =
        typeof(PropertyDefinition).GetProperty("CultureSpecificValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

    /// <summary>The CMS type the sync would give the property; null when it can't say (the sync itself then fails on it).</summary>
    private static PropertyDefinitionType? ResolveType(PropertyDefinitionModel model, Func<PropertyDefinitionModel, PropertyDefinitionType?> resolve)
    {
        try
        {
            return resolve(model);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? TypeName(PropertyDefinitionType? type) => type?.Name ?? type?.DefinitionType?.Name;

    private static string? BaseName(ContentTypeBase value) => value == ContentTypeBase.Undefined ? null : value.ToString();

    /// <summary>
    /// <c>Site.Models.ArticlePage, Site, Version=1.0.0.0, ...</c> as <c>Site.Models.ArticlePage, Site</c>, also inside the
    /// type arguments of a generic type, with the commas spaced alike.
    /// </summary>
    internal static string? WithoutVersion(string? assemblyQualifiedName) =>
        string.IsNullOrEmpty(assemblyQualifiedName)
            ? null
            : CommaSpacing().Replace(AssemblyDetails().Replace(assemblyQualifiedName, ""), ", ").Trim();

    /// <summary>The parts of an assembly name besides its simple name.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\s*,\s*(?:Version|Culture|PublicKeyToken|processorArchitecture)=[^,\]]*")]
    private static partial System.Text.RegularExpressions.Regex AssemblyDetails();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*,\s*")]
    private static partial System.Text.RegularExpressions.Regex CommaSpacing();

    /// <summary>The <c>Version=</c> of an assembly-qualified name, as major.minor (what the sync compares).</summary>
    internal static string? AssemblyVersion(string? assemblyQualifiedName)
    {
        var version = assemblyQualifiedName?.Split(',', StringSplitOptions.TrimEntries)
            .FirstOrDefault(p => p.StartsWith("Version=", StringComparison.OrdinalIgnoreCase))?["Version=".Length..];
        return version is not null && Version.TryParse(version, out var parsed) ? $"{parsed.Major}.{parsed.Minor}" : version;
    }
}
