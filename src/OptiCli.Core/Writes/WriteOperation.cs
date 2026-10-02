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

    /// <summary>
    /// create, block and upload: the new content's GUID, the same in every database the step runs against (a plan's
    /// <c>guid</c>, or derived from <c>guidNamespace</c> and <see cref="Id"/>). Null: the CMS picks one.
    /// </summary>
    public Guid? ContentGuid { get; init; }

    /// <summary>
    /// For an operation that publishes: also publish the unpublished changes someone other than opticli saved after the
    /// published version (<c>--include-draft</c>, a plan's <c>includeDraft</c>). Without it such a publish fails with a
    /// <c>conflict</c> whose details say what they are.
    /// </summary>
    public bool IncludeDraft { get; init; }

    /// <summary>
    /// Where a content approval sequence applies: start it instead of publishing (<c>--request-approval</c>, a plan's
    /// <c>requestApproval</c>). A publish there is refused without it. With a publish, content without a sequence is
    /// published as usual; alone, it is an error there.
    /// </summary>
    public bool RequestApproval { get; init; }

    /// <summary>
    /// set, area, create and publish: schedule the publish for this time instead of publishing now (<c>--publish-at</c>, a
    /// plan's <c>publishAt</c>). The rules for a publish apply.
    /// </summary>
    public DateTimeOffset? PublishAt { get; init; }

    /// <summary>True for an operation that publishes, now or at <see cref="PublishAt"/> (or would, but for an approval sequence).</summary>
    public virtual bool Publishes => PublishAt is not null;

    /// <summary>Every ref-valued field, so a plan can resolve <c>$id</c>s in them.</summary>
    public abstract IEnumerable<string?> Refs { get; }

    /// <summary>A copy with every ref and property value passed through <paramref name="map"/>.</summary>
    public abstract WriteOperation MapRefs(Func<string, string> map);

    /// <summary>A copy that publishes, for ops that can (<c>apply --publish</c>).</summary>
    public virtual WriteOperation WithPublish() => this;

    /// <summary>A copy that requests approval where it publishes (<c>apply --request-approval</c>).</summary>
    public WriteOperation WithRequestApproval() => Publishes ? this with { RequestApproval = true } : this;

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

/// <param name="BaseVersion">
/// Version the change must be based on, which must be the latest; default: the latest, read just before the call. With
/// <see cref="From"/>, only the version expected to be the latest.
/// </param>
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

    /// <summary>
    /// Base the change on this version instead of the latest (<c>--from</c>, a plan's <c>from</c>); the concurrency check
    /// still expects <see cref="BaseVersion"/>, else the latest. Not with a ref that names a version.
    /// </summary>
    public FromVersion? From { get; init; }

    /// <summary>
    /// Dry run only: area edits applied after the properties, for a plan step dry-run as the content will be after
    /// earlier steps (<see cref="PlanSimulation.OnExisting"/>). Not null also allows a set of nothing, which dry-runs the
    /// latest version as it is.
    /// </summary>
    public IReadOnlyList<AreaEdit>? AreaEdits { get; init; }

    public override IEnumerable<string?> Refs => [Ref];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref), Properties = MapValues(Properties, map) };

    // A step scheduled for later stays scheduled.
    public override WriteOperation WithPublish() => PublishAt is null ? this with { Publish = true } : this;

    public override bool Publishes => Publish || PublishAt is not null;
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

    /// <summary>
    /// Dry run only: check that the type is allowed below this type instead of below <see cref="Parent"/>'s, for a plan
    /// step whose real parent is created by an earlier step (<see cref="PlanSimulation"/>).
    /// </summary>
    public string? PlannedParentType { get; init; }

    public override IEnumerable<string?> Refs => [Parent];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Parent = map(Parent), Properties = MapValues(Properties, map) };

    // A step scheduled for later stays scheduled.
    public override WriteOperation WithPublish() => PublishAt is null ? this with { Publish = true } : this;

    public override bool Publishes => Publish || PublishAt is not null;
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

    /// <summary>As <see cref="SetOperation.From"/>.</summary>
    public FromVersion? From { get; init; }

    public override IEnumerable<string?> Refs => [Ref, Item];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref), Item = Map(Item, map) };

    // A step scheduled for later stays scheduled.
    public override WriteOperation WithPublish() => PublishAt is null ? this with { Publish = true } : this;

    public override bool Publishes => Publish || PublishAt is not null;
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

    // A step scheduled for later stays scheduled.
    public override WriteOperation WithPublish() => PublishAt is null ? this with { Publish = true } : this;

    public override bool Publishes => Publish || PublishAt is not null;
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

    /// <summary>
    /// Replace the file of this existing media item instead (<c>--replace</c>): a new version with the file, of the same
    /// media type. Without <see cref="For"/> and <see cref="Parent"/>.
    /// </summary>
    public string? Replace { get; init; }

    public override IEnumerable<string?> Refs => [For, Parent, Replace];

    public override WriteOperation MapRefs(Func<string, string> map) =>
        this with { For = Map(For, map), Parent = Map(Parent, map), Replace = Map(Replace, map), Properties = MapValues(Properties, map) };

    // A step scheduled for later stays scheduled.
    public override WriteOperation WithPublish() => PublishAt is null ? this with { Publish = true } : this;

    public override bool Publishes => Publish || PublishAt is not null;
}

public sealed record TranslateOperation(
    string Ref,
    string Lang,
    string? Name = null,
    JsonObject? Properties = null,
    bool Publish = false) : WriteOperation
{
    public override string Kind => "translate";

    /// <summary>
    /// Also give every block in the content's "For this page" folder the branch (a copy of its master language, saved or
    /// published as the content is), so the new branch doesn't show blocks in another language.
    /// </summary>
    public bool WithBlocks { get; init; }

    /// <summary>Delete the branch with all its versions instead of creating it. It can't be undone.</summary>
    public bool Remove { get; init; }

    /// <summary>Confirms <see cref="Remove"/> (<c>--confirm</c>); without it a removal asks on a terminal, and elsewhere fails with a <c>conflict</c>.</summary>
    public bool Confirm { get; init; }

    public override IEnumerable<string?> Refs => [Ref];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref), Properties = MapValues(Properties, map) };

    // A step scheduled for later stays scheduled.
    public override WriteOperation WithPublish() => PublishAt is null ? this with { Publish = true } : this;

    public override bool Publishes => Publish || PublishAt is not null;
}

/// <param name="Version">Version id to publish; default: the ref's version, else the latest in the language.</param>
public sealed record PublishOperation(string Ref, int? Version = null, string? Lang = null) : WriteOperation
{
    public override string Kind => "publish";

    public override bool Publishes => true;

    public override IEnumerable<string?> Refs => [Ref];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref) };
}

/// <summary>
/// Takes a published branch offline, as the edit UI's expiry does: a copy of the published version that stops publishing
/// now is published. Drafts are left as they are.
/// </summary>
public sealed record UnpublishOperation(string Ref, string? Lang = null) : WriteOperation
{
    public override string Kind => "unpublish";

    public override IEnumerable<string?> Refs => [Ref];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Ref = map(Ref) };
}

/// <summary>Deletes one unpublished version, which can't be undone.</summary>
/// <param name="Version">The version; default: the ref's version, else the newest version in the language.</param>
public sealed record DiscardOperation(string Ref, int? Version = null, string? Lang = null) : WriteOperation
{
    public override string Kind => "discard";

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
/// <param name="IgnoreReferences">
/// Delete even when other content references it or its descendants (<c>--ignore-references</c>, a plan's
/// <c>ignoreReferences</c>); without it such a delete asks on a terminal, and elsewhere fails with a <c>conflict</c>.
/// </param>
public sealed record DeleteOperation(string Ref, bool IgnoreReferences = false) : WriteOperation
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
