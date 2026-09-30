using System.Text.Json.Nodes;

namespace OptiCli.Core.Writes;

/// <summary>
/// One write, as a command line or a plan step describes it. Refs are as the user typed them (ids,
/// versions, GUIDs, URLs, or <c>$id</c> of content created earlier in a plan); the executor resolves them.
/// </summary>
public abstract record WriteOperation
{
    /// <summary>The plan's <c>op</c> name.</summary>
    public abstract string Kind { get; }

    /// <summary>Plan-local name of the content a create step makes, referenced by later steps as <c>$id</c>.</summary>
    public string? Id { get; init; }

    /// <summary>Every ref-valued field, so a plan can resolve <c>$id</c>s in them.</summary>
    public abstract IEnumerable<string?> Refs { get; }

    /// <summary>A copy with every ref and property value passed through <paramref name="map"/>.</summary>
    public abstract WriteOperation MapRefs(Func<string, string> map);

    /// <summary>A copy that publishes, for ops that can (<c>apply --publish</c>).</summary>
    public virtual WriteOperation WithPublish() => this;

    protected static string? Map(string? value, Func<string, string> map) => value is null ? null : map(value);

    protected static JsonObject? MapValues(JsonObject? values, Func<string, string> map)
    {
        if (values is null)
        {
            return null;
        }
        var copy = (JsonObject)values.DeepClone();
        MapNode(copy, map);
        return copy;
    }

    private static void MapNode(JsonNode? node, Func<string, string> map)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is JsonValue leaf && leaf.TryGetValue<string>(out var text))
                    {
                        obj[key] = map(text);
                    }
                    else
                    {
                        MapNode(value, map);
                    }
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue leaf && leaf.TryGetValue<string>(out var text))
                    {
                        array[i] = map(text);
                    }
                    else
                    {
                        MapNode(array[i], map);
                    }
                }
                break;
        }
    }
}

/// <param name="BaseVersion">Version the change must be based on; default: the latest, read just before the call.</param>
/// <param name="Force">Skip the concurrency check.</param>
public sealed record SetOperation(
    string Ref,
    JsonObject? Properties = null,
    string? Name = null,
    string? Lang = null,
    bool Publish = false,
    int? BaseVersion = null,
    bool Force = false) : WriteOperation
{
    public override string Kind => "set";

    public override IEnumerable<string?> Refs => [Ref];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref), Properties = MapValues(Properties, map) };

    public override WriteOperation WithPublish() => this with { Publish = true };
}

public sealed record CreateOperation(
    string Parent,
    string Type,
    string Name,
    JsonObject? Properties = null,
    string? Lang = null,
    bool Publish = false) : WriteOperation
{
    public override string Kind => "create";

    public override IEnumerable<string?> Refs => [Parent];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Parent = map(Parent), Properties = MapValues(Properties, map) };

    public override WriteOperation WithPublish() => this with { Publish = true };
}

/// <summary><c>area &lt;ref&gt; &lt;Prop&gt; add|remove|move</c>.</summary>
/// <param name="Action"><c>add</c>, <c>remove</c> or <c>move</c>.</param>
/// <param name="Item">add: the block to add; remove/move: the item, by the content it references (alternative to <paramref name="Index"/>).</param>
/// <param name="Index">remove/move: zero-based position of the item.</param>
/// <param name="At">add: insert position (default: end).</param>
/// <param name="To">move: target position.</param>
public sealed record AreaEdit(
    string Ref,
    string Property,
    string Action,
    string? Item = null,
    int? Index = null,
    int? At = null,
    int? To = null,
    string? Display = null,
    string? Lang = null,
    bool Publish = false,
    int? BaseVersion = null,
    bool Force = false) : WriteOperation
{
    public override string Kind => "area";

    public override IEnumerable<string?> Refs => [Ref, Item];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref), Item = Map(Item, map) };

    public override WriteOperation WithPublish() => this with { Publish = true };
}

/// <summary>A shared block: in <paramref name="For"/>'s "For this page" folder, or under <paramref name="Parent"/>.</summary>
public sealed record BlockCreateOperation(
    string Type,
    string Name,
    string? For = null,
    string? Parent = null,
    JsonObject? Properties = null,
    string? Lang = null,
    bool Publish = false) : WriteOperation
{
    public override string Kind => "block";

    public override IEnumerable<string?> Refs => [For, Parent];

    public override WriteOperation MapRefs(Func<string, string> map) =>
        this with { For = Map(For, map), Parent = Map(Parent, map), Properties = MapValues(Properties, map) };

    public override WriteOperation WithPublish() => this with { Publish = true };
}

/// <summary>A file as new media, in <paramref name="For"/>'s "For this page" folder or under the folder <paramref name="Parent"/>.</summary>
/// <param name="File">Path of the file to upload; in a plan, relative to the plan file.</param>
/// <param name="Type">Media type; default: the one the CMS maps the file's extension to.</param>
/// <param name="Name">Content name; default: the file name.</param>
public sealed record UploadOperation(
    string File,
    string? For = null,
    string? Parent = null,
    string? Name = null,
    string? Type = null,
    JsonObject? Properties = null,
    bool Publish = false) : WriteOperation
{
    public override string Kind => "upload";

    public override IEnumerable<string?> Refs => [For, Parent];

    public override WriteOperation MapRefs(Func<string, string> map) =>
        this with { For = Map(For, map), Parent = Map(Parent, map), Properties = MapValues(Properties, map) };

    public override WriteOperation WithPublish() => this with { Publish = true };
}

public sealed record TranslateOperation(
    string Ref,
    string Lang,
    string? Name = null,
    JsonObject? Properties = null,
    bool Publish = false) : WriteOperation
{
    public override string Kind => "translate";

    public override IEnumerable<string?> Refs => [Ref];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref), Properties = MapValues(Properties, map) };

    public override WriteOperation WithPublish() => this with { Publish = true };
}

/// <param name="Version">Version id to publish; default: the ref's version, else the latest in the language.</param>
public sealed record PublishOperation(string Ref, int? Version = null, string? Lang = null) : WriteOperation
{
    public override string Kind => "publish";

    public override IEnumerable<string?> Refs => [Ref];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref) };
}

public sealed record MoveOperation(string Ref, string To) : WriteOperation
{
    public override string Kind => "move";

    public override IEnumerable<string?> Refs => [Ref, To];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref), To = map(To) };
}

/// <summary>Moves content (and its descendants) to the recycle bin.</summary>
public sealed record DeleteOperation(string Ref) : WriteOperation
{
    public override string Kind => "delete";

    public override IEnumerable<string?> Refs => [Ref];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref) };
}

/// <summary>
/// Changes one item's access rights. <paramref name="Grant"/> and <paramref name="GrantUsers"/> map a role or user name
/// to levels (<c>Read,Edit</c>, <c>FullAccess</c>) and set that entry to exactly those; <paramref name="Revoke"/> removes
/// entries by name.
/// </summary>
/// <param name="BreakInheritance">Copy the inherited entries onto the item first; needed to change an inherited ACL.</param>
/// <param name="Inherit">Drop the item's own entries so it inherits from its parent again.</param>
public sealed record AccessOperation(
    string Ref,
    IReadOnlyDictionary<string, string>? Grant = null,
    IReadOnlyDictionary<string, string>? GrantUsers = null,
    IReadOnlyList<string>? Revoke = null,
    bool BreakInheritance = false,
    bool Inherit = false,
    bool AllowUnknownRole = false) : WriteOperation
{
    public override string Kind => "access";

    public override IEnumerable<string?> Refs => [Ref];

    // Role and user names are never refs, so only the target is mapped.
    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref) };
}
