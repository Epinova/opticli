using System.Data.Common;
using EPiServer.Core;
using EPiServer.Data;
using EPiServer.Data.Providers;
using EPiServer.DataAbstraction;
using EPiServer.DataAbstraction.RuntimeModel;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Protocol;

namespace OptiCli.Agent.Orphans;

/// <summary>A content type as the running site has it.</summary>
/// <param name="ModelType">The class the CMS has on record (<c>ModelTypeString</c>); null for a type made in admin mode.</param>
/// <param name="HasClass">The site can load that class.</param>
/// <param name="AllowedChildren">The types it allows below it, when it names them (admin mode's "Available content types").</param>
internal sealed record SiteType(
    int Id,
    Guid Guid,
    string Name,
    string Base,
    string? DisplayName,
    string? Description,
    string? ModelType,
    bool HasClass,
    IReadOnlyList<SiteProperty> Properties,
    IReadOnlyList<string>? AllowedChildren = null);

/// <summary>A property definition as the running site has it.</summary>
/// <param name="ExistsOnModel">The CMS's own flag: false once the model sync found it gone from the code (or for one made in admin mode).</param>
/// <param name="InModel">The site's model of the type (its class) has a property of this name.</param>
/// <param name="BlockType">For a block property: its block type's GUID.</param>
internal sealed record SiteProperty(int Id, int TypeId, string Name, bool ExistsOnModel, bool InModel, Guid? BlockType, RemovedPropertyDefinition Record);

/// <summary>The content model of the running site, and the two deletes; a stand-in in the tests.</summary>
internal interface IContentModelSource
{
    IReadOnlyList<SiteType> Types();

    /// <summary>Content and values that use the type, apart from properties that have it as their block type.</summary>
    TypeUsage Usage(SiteType type);

    /// <summary>Everything the CMS deletes with the property: its values, values inside it, category selections.</summary>
    StoredValueCounts Values(SiteProperty property);

    void Remove(SiteProperty property);

    void Remove(SiteType type);
}

/// <summary>
/// <see cref="IContentModelSource"/> through the CMS: <see cref="IContentTypeRepository"/> and
/// <see cref="IPropertyDefinitionRepository"/> for the model and the deletes, the CMS's own usage check, and counts read
/// with the CMS's database executor.
/// </summary>
/// <remarks>
/// The counts are fixed SELECTs: the CMS has no API that counts what its deletes remove (a property's values in local
/// blocks and category selections, a page-type property's values naming a type). Nothing here writes but the two
/// repository deletes.
/// </remarks>
internal sealed class ContentModelSource(IServiceProvider services) : IContentModelSource
{
    /// <summary>Content of a type, in and out of the recycle bin, and versions whose page-type properties name it.</summary>
    private const string UsageSql = """
        SELECT
            (SELECT COUNT(*) FROM tblContent WHERE fkContentTypeID = @id AND Deleted = 0) AS Content,
            (SELECT COUNT(*) FROM tblContent WHERE fkContentTypeID = @id AND Deleted = 1) AS InRecycleBin,
            (SELECT COUNT(DISTINCT wp.fkWorkContentID)
             FROM tblWorkContentProperty wp
             INNER JOIN tblPropertyDefinition pd ON pd.pkID = wp.fkPropertyDefinitionID
             WHERE pd.Property = 3 AND wp.ContentType = @id) AS PageTypeValues
        """;

    /// <summary>Inline blocks (CMS 12.2x and later); the table doesn't exist before.</summary>
    private const string InlineSql = """
        IF OBJECT_ID(N'dbo.tblInlineBlockUsage') IS NULL
            SELECT 0 AS Uses
        ELSE
            EXEC sp_executesql N'SELECT COUNT(DISTINCT fkContentID) AS Uses FROM dbo.tblInlineBlockUsage WHERE fkContentTypeID = @id', N'@id int', @id
        """;

    private readonly IContentTypeRepository _types = services.GetRequiredService<IContentTypeRepository>();
    private readonly IPropertyDefinitionRepository _properties = services.GetRequiredService<IPropertyDefinitionRepository>();

    public IReadOnlyList<SiteType> Types()
    {
        var models = services.GetRequiredService<ContentTypeModelRepository>();
        var available = services.GetService<IAvailableSettingsRepository>();
        var all = _types.List().ToList();
        var blockNames = all.ToDictionary(t => t.GUID, t => t.Name);
        return all.Select(t => new SiteType(
                t.ID,
                t.GUID,
                t.Name,
                t.Base.ToString(),
                t.DisplayName,
                t.Description,
                string.IsNullOrWhiteSpace(t.ModelTypeString) ? null : t.ModelTypeString,
                t.ModelType is not null,
                t.PropertyDefinitions.Select(p => Property(t, p, models, blockNames)).ToList(),
                AllowedChildren(available, t)))
            .ToList();
    }

    public TypeUsage Usage(SiteType type)
    {
        var (content, bin, pageTypes) = Query(UsageSql, type.Id, r => (Int(r, "Content"), Int(r, "InRecycleBin"), Int(r, "PageTypeValues")));
        var inline = Query(InlineSql, type.Id, r => Int(r, "Uses"));
        var usage = new TypeUsage(content, bin, inline, pageTypes, []);
        if (!usage.InUse && _types.Load(type.Id) is { } contentType)
        {
            // The CMS's own check, which its Delete runs too: what the counts above don't see (content providers, values
            // of a block type's properties stored in other content).
            var others = services.GetRequiredService<IContentModelUsage>().ListContentOfContentType(contentType);
            usage = usage with { OtherUses = others.Select(u => u.ContentLink.ID).Distinct().Count() };
        }
        return usage;
    }

    public StoredValueCounts Values(SiteProperty property) =>
        Query(OrphanRemoval.PropertyValuesSql(property.BlockType is not null), property.Id, r => new StoredValueCounts(Int(r, "Content"), Int(r, "Versions")));

    public void Remove(SiteProperty property) =>
        _properties.Delete(_properties.Load(property.Id) ?? throw AgentExceptionFor(property.Record.Name));

    public void Remove(SiteType type) =>
        _types.Delete(_types.Load(type.Id) ?? throw AgentExceptionFor(type.Name));

    private static Cms.AgentException AgentExceptionFor(string name) => Cms.AgentException.Conflict($"'{name}' was removed meanwhile.");

    private static SiteProperty Property(ContentType type, PropertyDefinition property, ContentTypeModelRepository models, IReadOnlyDictionary<Guid, string> blockNames)
    {
        var block = (property.Type as BlockPropertyDefinitionType)?.BlockType?.GUID;
        return new SiteProperty(
            property.ID,
            type.ID,
            property.Name,
            property.ExistsOnModel,
            models.GetPropertyModel(type.ID, property) is not null,
            block,
            new RemovedPropertyDefinition(
                property.ID,
                property.Name,
                property.Type?.DataType.ToString(),
                property.Type?.TypeName,
                block is { } guid ? blockNames.GetValueOrDefault(guid) ?? guid.ToString() : null,
                property.LanguageSpecific,
                property.Required,
                property.Searchable,
                property.DisplayEditUI,
                property.EditCaption,
                property.HelpText,
                property.Tab?.Name,
                property.FieldOrder));
    }

    private static IReadOnlyList<string>? AllowedChildren(IAvailableSettingsRepository? available, ContentType type)
    {
        var setting = available?.GetSetting(type);
        return setting is { Availability: Availability.Specific } ? setting.AllowedContentTypeNames.Order(StringComparer.OrdinalIgnoreCase).ToList() : null;
    }

    private T Query<T>(string sql, int id, Func<DbDataReader, T> read)
    {
        var executor = services.GetRequiredService<IDatabaseExecutorFactory>().CreateDefaultHandler();
        return executor.Execute(() =>
        {
            using var command = executor.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(executor.CreateParameter("id", id));
            using var reader = command.ExecuteReader();
            return reader.Read() ? read(reader) : throw new InvalidOperationException("The count query returned no row.");
        });
    }

    private static int Int(DbDataReader reader, string column) => Convert.ToInt32(reader[column], System.Globalization.CultureInfo.InvariantCulture);
}
