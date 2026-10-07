using System.Text.Json;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <param name="Key">What a composition node stores in <c>displaySettings</c> for the setting.</param>
public sealed record DisplaySettingChoiceInfo(string Key, string? Name);

/// <param name="Key">The setting's key in a node's <c>displaySettings</c>.</param>
/// <param name="Editor"><c>select</c> (one of <paramref name="Choices"/>) or <c>checkbox</c> (<c>true</c>/<c>false</c>).</param>
public sealed record DisplaySettingInfo(string Key, string? Name, string? Editor, IReadOnlyList<DisplaySettingChoiceInfo>? Choices);

/// <summary>A Visual Builder display template (CMS 13): a named style with settings that a composition node can use.</summary>
/// <param name="Key">What a node stores as its <c>displayTemplate</c>.</param>
/// <param name="NodeType">The node type it is for (<c>experience</c>, <c>section</c>, <c>row</c>, <c>column</c>, <c>component</c>); null for any.</param>
/// <param name="BaseType">The content type base it is for (<c>Block</c>, <c>Section</c>, ...); null for any.</param>
/// <param name="ContentType">The content type it is for; null for any.</param>
/// <param name="IsDefault">The CMS's default template for what it is for.</param>
public sealed record DisplayTemplateInfo(
    string Key, string? Name, string? NodeType, string? BaseType, string? ContentType, bool IsDefault, IReadOnlyList<DisplaySettingInfo> Settings)
{
    /// <summary>
    /// Whether a node of <paramref name="nodeType"/> holding content of <paramref name="type"/> may use it, by the rules the
    /// CMS validates a composition with (<c>LayoutDisplaySettingsValidator</c>): the node type, the content type and the base
    /// it names, each when it names one.
    /// </summary>
    public bool AppliesTo(ContentTypeInfo? type, string nodeType) =>
        (NodeType is null || NodeType.Equals(nodeType, StringComparison.OrdinalIgnoreCase))
        && (ContentType is null || ContentType.Equals(type?.Name, StringComparison.Ordinal))
        && (BaseType is null || BaseType.Equals(type?.Base, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The composition node a content type's content sits in: an experience is the root, a section a section, and a block
    /// (an element, or a section-enabled block in an outline) a component. Null for anything else.
    /// </summary>
    public static string? NodeTypeOf(ContentTypeInfo type) => type.Kind switch
    {
        ContentKind.Experience => "experience",
        ContentKind.Section => "section",
        ContentKind.Element => "component",
        ContentKind.Block when type.CompositionBehaviors.Count > 0 => "component",
        _ => null,
    };
}

/// <summary>
/// <c>display-templates</c> and <c>type</c>'s <c>displayTemplates</c>: CMS 13's Visual Builder display templates and their
/// settings (<c>tblDisplayTemplate</c>, <c>tblDisplaySetting</c>), which are data, not code. None on CMS 12.
/// </summary>
public static class DisplayTemplateReader
{
    private const string TemplatesSql = """
        SELECT t.pkID, t.DisplayTemplateKey, t.Name, t.NodeType, t.BaseType, t.ContentTypeID, CONVERT(bit, ISNULL(t.IsDefault, 0)) AS IsDefault
        FROM tblDisplayTemplate t
        ORDER BY t.DisplayTemplateKey
        """;

    private const string SettingsSql = """
        SELECT fkTemplateId, DisplaySettingKey, Name, Editor, Choices
        FROM tblDisplaySetting
        ORDER BY fkTemplateId, SortOrder, DisplaySettingKey
        """;

    public static async Task<IReadOnlyList<DisplayTemplateInfo>> ListAsync(CmsDatabase db, CmsModel model, CancellationToken cancellationToken)
    {
        if (!model.Schema.Compositions)
        {
            return [];
        }
        var settings = (await db.QueryAsync(SettingsSql, r => (
                Template: r.GetInt32("fkTemplateId"),
                Setting: new DisplaySettingInfo(r.GetString("DisplaySettingKey"), r.GetStringOrNull("Name"), r.GetStringOrNull("Editor"), Choices(r.GetStringOrNull("Choices")))),
            cancellationToken))
            .ToLookup(s => s.Template, s => s.Setting);
        return await db.QueryAsync(TemplatesSql, r => new DisplayTemplateInfo(
            r.GetString("DisplayTemplateKey"),
            r.GetStringOrNull("Name"),
            r.GetStringOrNull("NodeType") is { Length: > 0 } nodeType ? nodeType : null,
            r.GetStringOrNull("BaseType") is { Length: > 0 } baseType ? baseType : null,
            r.GetInt32OrNull("ContentTypeID") is { } typeId ? model.TypeName(typeId) : null,
            r.GetBoolean(r.GetOrdinal("IsDefault")),
            settings[r.GetInt32("pkID")].ToList()), cancellationToken);
    }

    /// <summary>A select setting's choices, stored as JSON (<c>[{"key":"light","name":"Light","sortOrder":0}]</c>), in their order; null for none.</summary>
    internal static IReadOnlyList<DisplaySettingChoiceInfo>? Choices(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var choices = document.RootElement.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("key", out _))
                .Select(c => (
                    Order: c.TryGetProperty("sortOrder", out var order) && order.TryGetInt32(out var number) ? number : 0,
                    Choice: new DisplaySettingChoiceInfo(c.GetProperty("key").GetString() ?? "", c.TryGetProperty("name", out var name) ? name.GetString() : null)))
                .OrderBy(c => c.Order)
                .Select(c => c.Choice)
                .ToList();
            return choices.Count == 0 ? null : choices;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
