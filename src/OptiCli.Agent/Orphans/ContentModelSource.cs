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
/// <param name="ModelType">The class the CMS has on record (<c>ModelTypeString</c>); null for a type made in admin mode, and for a CMS 13 type with a GUID (see <see cref="SyncedFromCode"/>).</param>
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
    IReadOnlyList<string>? AllowedChildren = null)
{
    /// <summary>
    /// CMS 13: the model sync made it from a class it recorded no name for (a model with a GUID), so it came from code even
    /// without a <see cref="ModelType"/> (<see cref="Compat.AgentBuild.FromCode"/>).
    /// </summary>
    public bool SyncedFromCode { get; init; }

    /// <summary>
    /// CMS 13: neither a class nor a model-sync version on record, and no class of the site has its GUID or name: made in
    /// admin mode, or a code type a content import overwrote (<see cref="Compat.AgentBuild.OriginUnknown"/>).
    /// </summary>
    public bool OriginUnknown { get; init; }

    /// <summary>Made from code, not in admin mode: a class on record, or <see cref="SyncedFromCode"/>.</summary>
    public bool FromCode => ModelType is not null || SyncedFromCode;
}

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
/// <see cref="IPropertyDefinitionRepository"/> for the model and the deletes (a property's through its type's save on
/// CMS 13, <see cref="Compat.AgentBuild.DeleteProperty"/>), the CMS's own usage check, and counts read with the CMS's
/// database executor.
/// </summary>
/// <remarks>
/// The counts are fixed SELECTs: the CMS has no API that counts what its deletes remove (a property's values in local
/// blocks and category selections, a page-type property's values naming a type). Nothing here writes but the two
/// repository deletes.
/// </remarks>
internal sealed class ContentModelSource(IServiceProvider services) : IContentModelSource
{
    /// <summary>
    /// Content of a type, in and out of the recycle bin, and the page-type property values (data type 3) naming it, in
    /// versions and on content: <c>netContentTypeDelete</c> clears both.
    /// </summary>
    private const string UsageSql = """
        SELECT
            (SELECT COUNT(*) FROM tblContent WHERE fkContentTypeID = @id AND Deleted = 0) AS Content,
            (SELECT COUNT(*) FROM tblContent WHERE fkContentTypeID = @id AND Deleted = 1) AS InRecycleBin,
            (SELECT COUNT(*)
             FROM tblWorkContentProperty wp
             INNER JOIN tblPropertyDefinition pd ON pd.pkID = wp.fkPropertyDefinitionID
             WHERE pd.Property = 3 AND wp.ContentType = @id)
            + (SELECT COUNT(*)
             FROM tblContentProperty cp
             INNER JOIN tblPropertyDefinition pd ON pd.pkID = cp.fkPropertyDefinitionID
             WHERE pd.Property = 3 AND cp.ContentType = @id) AS PageTypeValues
        """;

    /// <summary>Where those page-type values are: versions, and content whose value has no version row.</summary>
    private const string PageTypeRefsSql = """
        SELECT DISTINCT TOP (@max) ContentId, WorkId FROM (
            SELECT wc.fkContentID AS ContentId, wc.pkID AS WorkId
            FROM tblWorkContentProperty wp
            INNER JOIN tblPropertyDefinition pd ON pd.pkID = wp.fkPropertyDefinitionID
            INNER JOIN tblWorkContent wc ON wc.pkID = wp.fkWorkContentID
            WHERE pd.Property = 3 AND wp.ContentType = @id
            UNION
            SELECT cp.fkContentID, NULL
            FROM tblContentProperty cp
            INNER JOIN tblPropertyDefinition pd ON pd.pkID = cp.fkPropertyDefinitionID
            WHERE pd.Property = 3 AND cp.ContentType = @id
              AND NOT EXISTS (SELECT 1 FROM tblWorkContent wc
                              INNER JOIN tblWorkContentProperty wp ON wp.fkWorkContentID = wc.pkID AND wp.fkPropertyDefinitionID = cp.fkPropertyDefinitionID
                              WHERE wc.fkContentID = cp.fkContentID AND wp.ContentType = @id)) refs
        ORDER BY ContentId, WorkId
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
        var hasModel = Compat.AgentBuild.HasPropertyModel(services);
        var inBuild = Compat.AgentBuild.InBuild(services);
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
                t.PropertyDefinitions.Select(p => Property(t, p, hasModel, blockNames)).ToList(),
                AllowedChildren(available, t))
            {
                SyncedFromCode = Compat.AgentBuild.FromCode(t),
                OriginUnknown = Compat.AgentBuild.OriginUnknown(t, inBuild),
            })
            .ToList();
    }

    public TypeUsage Usage(SiteType type)
    {
        var (content, bin, pageTypes) = Query(UsageSql, type.Id, r => (Int(r, "Content"), Int(r, "InRecycleBin"), Int(r, "PageTypeValues")));
        var inline = Query(InlineSql, type.Id, r => Int(r, "Uses"));
        var usage = new TypeUsage(content, bin, inline, pageTypes, []);
        if (pageTypes > 0)
        {
            usage = usage with
            {
                PageTypeVersions = QueryRows(PageTypeRefsSql, type.Id, r => r["WorkId"] is DBNull ? $"{Int(r, "ContentId")}" : $"{Int(r, "ContentId")}_{Int(r, "WorkId")}"),
            };
        }
        if (!usage.InUse && _types.Load(type.Id) is { } contentType)
        {
            // The CMS's own check, which its Delete runs too: what the counts above don't see (content providers, values
            // of a block type's properties stored in other content).
            var others = services.GetRequiredService<IContentModelUsage>().ListContentOfContentType(contentType);
            usage = usage with { OtherUses = others.Select(u => u.ContentLink.ID).Distinct().Count() };
        }
        return usage;
    }

    public StoredValueCounts Values(SiteProperty property)
    {
        var counts = Query(OrphanRemoval.PropertyValuesSql(property.BlockType is not null), property.Id, r => new StoredValueCounts(Int(r, "Content"), Int(r, "Versions")));
        if (_properties.Load(property.Id) is not { } definition)
        {
            return counts;
        }
        // The counts read the CMS's own tables. Content providers (a catalog, a DAM) keep theirs elsewhere: ask each one,
        // and the CMS's own usage check, which asks them all, for what the counts can't see.
        var providers = new List<string>();
        services.GetRequiredService<IContentProviderManager>().ProviderMap.Iterate((Action<ContentProvider>)(provider =>
        {
            if (provider.IsDefaultProvider)
            {
                return;
            }
            try
            {
                if (provider.IsPropertyDefinitionUsed(definition))
                {
                    providers.Add(provider.Name);
                }
            }
            catch (Exception ex)
            {
                providers.Add($"{provider.Name} (it couldn't tell: {ex.GetType().Name})");
            }
        }));
        if (providers.Count == 0 && !counts.Any && services.GetRequiredService<IContentModelUsage>().IsPropertyDefinitionUsed(definition))
        {
            providers.Add(OrphanRemoval.UnknownProvider);
        }
        return providers.Count == 0 ? counts : counts with { Providers = providers };
    }

    public void Remove(SiteProperty property) =>
        Compat.AgentBuild.DeleteProperty(_types, _properties, _properties.Load(property.Id) ?? throw AgentExceptionFor(property.Record.Name));

    public void Remove(SiteType type) =>
        _types.Delete(_types.Load(type.Id) ?? throw AgentExceptionFor(type.Name));

    private static Cms.AgentException AgentExceptionFor(string name) => Cms.AgentException.Conflict($"'{name}' was removed meanwhile.");

    private static SiteProperty Property(ContentType type, PropertyDefinition property, Func<int, PropertyDefinition, bool> hasModel, IReadOnlyDictionary<Guid, string> blockNames)
    {
        var block = Compat.AgentBuild.BlockTypeGuid(property);
        return new SiteProperty(
            property.ID,
            type.ID,
            property.Name,
            property.ExistsOnModel,
            hasModel(type.ID, property),
            block,
            new RemovedPropertyDefinition(
                property.ID,
                property.Name,
                property.Type?.DataType.ToString(),
                property.Type?.TypeName,
                block is { } guid ? blockNames.GetValueOrDefault(guid) ?? guid.ToString() : null,
                property.LanguageSpecific,
                property.Required,
                Cms.Compat.CmsApi.IsSearchable(property),
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

    private IReadOnlyList<T> QueryRows<T>(string sql, int id, Func<DbDataReader, T> read)
    {
        var executor = services.GetRequiredService<IDatabaseExecutorFactory>().CreateDefaultHandler();
        return executor.Execute(() =>
        {
            using var command = executor.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(executor.CreateParameter("id", id));
            command.Parameters.Add(executor.CreateParameter("max", OrphanRemoval.MaxRefs));
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read())
            {
                rows.Add(read(reader));
            }
            return (IReadOnlyList<T>)rows;
        });
    }

    private static int Int(DbDataReader reader, string column) => Convert.ToInt32(reader[column], System.Globalization.CultureInfo.InvariantCulture);
}
