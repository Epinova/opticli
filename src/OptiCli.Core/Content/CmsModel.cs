using OptiCli.Core.Cms;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Text;
using OptiCli.Core.Urls;

namespace OptiCli.Core.Content;

/// <summary>
/// The small, slow-changing tables every content read needs (types, property definitions, languages,
/// sites), loaded once per invocation so decoders and URL builders can work without further queries.
/// </summary>
public sealed class CmsModel
{
    private const string PropertiesSql = """
        SELECT pd.pkID, ISNULL(pd.fkContentTypeID, 0) AS ContentTypeId, pd.Name, pdt.Name AS TypeName,
               pdt.Property AS BaseType, bt.pkID AS BlockTypeId, pd.LanguageSpecific, pd.IsList
        FROM tblPropertyDefinition pd
        JOIN tblPropertyDefinitionType pdt ON pdt.pkID = pd.fkPropertyDefinitionTypeID
        LEFT JOIN tblContentType bt ON bt.ContentTypeGUID = pdt.fkContentTypeGUID
        """;

    private const string LanguagesSql = """
        SELECT pkID, RTRIM(ISNULL(LanguageID, '')) AS Code, Name, URLSegment, Enabled
        FROM tblLanguageBranch
        ORDER BY SortIndex, LanguageID
        """;

    // The asset roots are system folders directly under the root, found by name rather than by
    // hard-coded GUIDs so nothing here depends on one installation's data.
    private const string AssetRootsSql = """
        SELECT c.pkID, cl.Name
        FROM tblContent c
        JOIN tblContentLanguage cl ON cl.fkContentID = c.pkID
        WHERE c.fkParentID = (SELECT MIN(pkID) FROM tblContent WHERE fkParentID IS NULL)
          AND cl.Name IN ('SysGlobalAssets', 'SysContentAssets')
        """;

    private const string CategoriesSql = "SELECT pkID, CategoryName FROM tblCategory";

    /// <summary><c>tblPropertyDefinition.LanguageSpecific</c> value for culture-specific properties.</summary>
    private const int CultureSpecificFlag = 4;

    private readonly Dictionary<int, ContentTypeInfo> _types;
    private readonly ILookup<int, PropertyDefinition> _propertiesByType;
    private readonly IReadOnlyDictionary<int, string> _categories;

    internal CmsModel(
        IReadOnlyList<ContentTypeInfo> types,
        IReadOnlyList<PropertyDefinition> properties,
        IReadOnlyList<LanguageBranch> languages,
        IReadOnlyList<SiteInfo> sites,
        int? globalAssetsRoot,
        int? contentAssetsRoot,
        IReadOnlyDictionary<int, string>? categories = null)
    {
        _categories = categories ?? new Dictionary<int, string>();
        _types = types.ToDictionary(t => t.Id);
        Types = types;
        Properties = properties.ToDictionary(p => p.Id);
        _propertiesByType = properties.ToLookup(p => p.ContentTypeId);
        Languages = languages;
        Sites = new SiteMap(sites, languages, globalAssetsRoot, contentAssetsRoot);
    }

    public IReadOnlyList<ContentTypeInfo> Types { get; }

    public IReadOnlyDictionary<int, PropertyDefinition> Properties { get; }

    public IReadOnlyList<LanguageBranch> Languages { get; }

    public SiteMap Sites { get; }

    public static async Task<CmsModel> LoadAsync(CmsDatabase db, CancellationToken cancellationToken)
    {
        var types = await ContentTypeReader.ListAsync(db, cancellationToken);
        var properties = await db.QueryAsync(PropertiesSql, r => new PropertyDefinition(
            r.GetInt32("pkID"),
            r.GetInt32("ContentTypeId"),
            r.GetString("Name"),
            r.GetString("TypeName"),
            (PropertyBaseType)r.GetInt32("BaseType"),
            r.GetInt32OrNull("BlockTypeId"),
            r.GetInt32OrNull("LanguageSpecific") == CultureSpecificFlag,
            r.GetBooleanOrNull("IsList") ?? false), cancellationToken);
        var languages = await db.QueryAsync(LanguagesSql, r => new LanguageBranch(
            r.GetInt32("pkID"),
            r.GetString("Code"),
            r.GetStringOrNull("Name"),
            r.GetStringOrNull("URLSegment"),
            r.GetBoolean(r.GetOrdinal("Enabled"))), cancellationToken);
        var sites = await SiteReader.ListAsync(db, cancellationToken);
        var roots = await db.QueryAsync(AssetRootsSql, r => (Id: r.GetInt32("pkID"), Name: r.GetString("Name")), cancellationToken);

        var categories = await db.QueryAsync(CategoriesSql, r => (Id: r.GetInt32("pkID"), Name: r.GetString("CategoryName")), cancellationToken);

        int? Root(string name) => roots.Where(r => r.Name == name).Select(r => (int?)r.Id).FirstOrDefault();
        return new CmsModel(types, properties, languages, sites, Root("SysGlobalAssets"), Root("SysContentAssets"),
            categories.ToDictionary(c => c.Id, c => c.Name));
    }

    public ContentTypeInfo? Type(int id) => _types.GetValueOrDefault(id);

    public string TypeName(int id) => _types.TryGetValue(id, out var type) ? type.Name : $"#{id}";

    public ContentKind Kind(int typeId) => _types.TryGetValue(typeId, out var type) ? type.Kind : ContentKind.Other;

    /// <summary>Properties defined on a content type (inherited ones are duplicated onto it by the CMS).</summary>
    public IEnumerable<PropertyDefinition> PropertiesOf(int contentTypeId) => _propertiesByType[contentTypeId];

    public LanguageBranch? Language(int id) => Languages.FirstOrDefault(l => l.Id == id);

    /// <summary>A category's name (<c>tblCategory.CategoryName</c>), as <c>set</c> takes it.</summary>
    public string CategoryName(int id) => _categories.TryGetValue(id, out var name) ? name : $"#{id}";

    public LanguageBranch? LanguageByCode(string code) =>
        Languages.FirstOrDefault(l => !l.IsInvariant && string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <exception cref="UsageException">Unknown language code; the hint lists close ones.</exception>
    public LanguageBranch RequireLanguage(string code)
    {
        return LanguageByCode(code) ?? throw new UsageException(
            $"No language '{code}'.",
            Suggestions.DidYouMean(code, Languages.Where(l => !l.IsInvariant).Select(l => l.Code))
                ?? $"Languages: {string.Join(", ", Languages.Where(l => l.Enabled && !l.IsInvariant).Select(l => l.Code))}.");
    }

    /// <exception cref="NotFoundException">Unknown type; the hint suggests close names.</exception>
    public ContentTypeInfo RequireType(string nameOrGuid) => ContentTypeLookup.Find(Types, nameOrGuid);
}
