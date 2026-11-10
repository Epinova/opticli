using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EPiServer.Core;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>One step of a <see cref="ContentPlan"/>, as the site's tool planned it.</summary>
internal abstract record PlanStep
{
    /// <summary>Its place in the plan, from 1.</summary>
    public required int Number { get; init; }

    public abstract ContentPlanKinds Kind { get; }

    /// <summary>The kind as results and the audit name it: <c>update</c>, <c>create</c>, <c>move</c>, <c>trash</c>.</summary>
    public string KindName => Kind.ToString().ToLowerInvariant();
}

/// <param name="Content">The content, with the version the change is based on (none: the latest).</param>
/// <param name="Language">The branch, as the content passed was loaded; null for content without languages.</param>
internal sealed record UpdateStep(ContentReference Content, string? Language, string? Name, JsonObject Properties, IReadOnlyList<PlanAreaOp> AreaOps) : PlanStep
{
    public override ContentPlanKinds Kind => ContentPlanKinds.Update;
}

/// <param name="Parent">Below this; or, with <paramref name="InAssets"/>, in its "For this page" folder.</param>
internal sealed record CreateStep(PlannedContent Handle, PlanTarget Parent, bool InAssets, string Type, string Name, string? Language, JsonObject Properties) : PlanStep
{
    public override ContentPlanKinds Kind => ContentPlanKinds.Create;

    /// <summary>
    /// For a dry run whose parent is planned content: its type, which the "allowed below" check uses, while
    /// <see cref="Parent"/> is the existing content it will be made below (a stand-in).
    /// </summary>
    public string? PlannedParentType { get; init; }
}

internal sealed record MoveStep(ContentReference Content, PlanTarget Destination) : PlanStep
{
    public override ContentPlanKinds Kind => ContentPlanKinds.Move;

    /// <summary>As <see cref="CreateStep.PlannedParentType"/>, for a destination the plan creates.</summary>
    public string? PlannedDestinationType { get; init; }

    /// <summary>For a dry run whose destination is planned: it is planned in the "For this page" folder of <see cref="Destination"/>.</summary>
    public bool DestinationInAssets { get; init; }
}

internal sealed record TrashStep(ContentReference Content) : PlanStep
{
    public override ContentPlanKinds Kind => ContentPlanKinds.Trash;
}

/// <summary>Existing content, or content an earlier step creates.</summary>
internal sealed record PlanTarget(ContentReference? Existing, PlannedContent? Planned)
{
    /// <summary>As results show it: the reference, or <c>step 2</c>.</summary>
    public override string ToString() => Existing is { } link ? link.ToString() : Planned!.ToString();
}

/// <summary>A ContentArea edit of an update (<see cref="AreaOperation"/>).</summary>
internal sealed record PlanAreaOp(string Op, string Property, PlanTarget Block, int? At, string? DisplayOption, bool IfMissing);

/// <summary>
/// Property values of a plan as JSON, in <c>update_content</c>'s forms, with <see cref="PlannedContent"/> in them: written
/// as a marker string with the plan's secret <see cref="ContentPlan.Nonce"/>, which no argument or content can contain,
/// and replaced by the new content's reference once it exists (<see cref="Replace"/>).
/// </summary>
internal static class PlanValues
{
    private const string Prefix = "\u0001opticli-planned:";

    public static string Marker(string nonce, int step) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{nonce}:{step}");

    /// <exception cref="ArgumentException">A value of the CMS's own types that has no JSON form here.</exception>
    public static JsonNode? ToJson(object? value, ContentPlan plan) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, Options(plan)),
    };

    /// <summary>The step a marker in <paramref name="node"/> stands for; null for any other value.</summary>
    public static int? Step(JsonNode? node, ContentPlan plan) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text
        && text.StartsWith(Prefix + plan.Nonce + ":", StringComparison.Ordinal)
        && int.TryParse(text.AsSpan(Prefix.Length + plan.Nonce.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var step)
            ? step
            : null;

    /// <summary>The steps whose content <paramref name="node"/> refers to, in order.</summary>
    public static IEnumerable<int> Steps(JsonNode? node, ContentPlan plan) => node switch
    {
        JsonObject obj => obj.SelectMany(p => Steps(p.Value, plan)),
        JsonArray array => array.SelectMany(n => Steps(n, plan)),
        _ => Step(node, plan) is { } step ? [step] : [],
    };

    /// <summary>A copy of <paramref name="node"/> with every marker replaced by what <paramref name="replace"/> gives for its step.</summary>
    public static JsonNode? Replace(JsonNode? node, ContentPlan plan, Func<int, JsonNode?> replace) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Replace(p.Value, plan, replace)))),
        JsonArray array => new JsonArray(array.Select(n => Replace(n, plan, replace)).ToArray()),
        _ => Step(node, plan) is { } step ? replace(step) : node.DeepClone(),
    };

    /// <summary>Property values for a call, as the operations take them.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Elements(JsonObject properties) =>
        properties.ToDictionary(p => p.Key, p => p.Value is null ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);

    private static JsonSerializerOptions Options(ContentPlan plan) => new(AgentJson.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ReferenceConverter(plan) },
    };

    /// <summary>Content references as the operations take them; refuses the CMS's own value types that have no such form.</summary>
    private sealed class ReferenceConverter(ContentPlan plan) : JsonConverterFactory
    {
        public override bool CanConvert(Type type) =>
            typeof(PlannedContent).IsAssignableFrom(type) || typeof(ContentReference).IsAssignableFrom(type) || typeof(IContentData).IsAssignableFrom(type)
            || typeof(XhtmlString).IsAssignableFrom(type) || typeof(EPiServer.Url).IsAssignableFrom(type) || typeof(ContentArea).IsAssignableFrom(type)
            || typeof(EPiServer.Core.PropertyData).IsAssignableFrom(type);

        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Writer<>).MakeGenericType(type), plan)!;

        private sealed class Writer<T>(ContentPlan plan) : JsonConverter<T>
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                switch (value)
                {
                    case PlannedContent planned:
                        writer.WriteStringValue(plan.Planned(planned, "value").Marker);
                        break;
                    case ContentReference link:
                        writer.WriteStringValue(link.ToString());
                        break;
                    case IContent content:
                        writer.WriteStringValue(content.ContentLink.ToReferenceWithoutVersion().ToString());
                        break;
                    case XhtmlString html:
                        writer.WriteStringValue(html.ToInternalString());
                        break;
                    case EPiServer.Url url:
                        writer.WriteStringValue(url.ToString());
                        break;
                    default:
                        throw new ArgumentException(
                            $"A {value!.GetType().Name} has no value form in a plan: give a ContentArea as a list of items (new {{ @ref = block }}), a block's values as an object, references as ContentReference, IContent or PlannedContent.");
                }
            }
        }
    }

    /// <summary>The handles <paramref name="step"/> uses (parent, destination, values, area items), by step.</summary>
    public static IEnumerable<int> Uses(PlanStep step, ContentPlan plan) => step switch
    {
        UpdateStep update => Steps(update.Properties, plan).Concat(update.AreaOps.Where(a => a.Block.Planned is not null).Select(a => a.Block.Planned!.Step)),
        CreateStep create => Steps(create.Properties, plan).Concat(create.Parent.Planned is { } parent ? [parent.Step] : []),
        MoveStep move => move.Destination.Planned is { } destination ? [destination.Step] : [],
        _ => [],
    };
}
