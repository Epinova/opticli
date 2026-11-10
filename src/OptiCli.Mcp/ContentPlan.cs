using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPiServer.Core;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp;

/// <summary>
/// What a write tool changes (<see cref="IOptiCliWriteTool{TArgs}.PlanAsync"/>): steps the module checks and then runs in
/// order, as the editor, through its own content operations, every one a draft. A step can use content an earlier step
/// creates (<see cref="PlannedContent"/>) as a parent, a destination, or a reference in a property value.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here changes anything: the module does the dry run and the real run itself. Before saving the first step
/// it checks them all; a step that fails stops the run, and the steps before it stay saved (there is no transaction
/// across CMS saves). Nothing in a plan publishes, and nothing deletes for good: trash is the recycle bin.
/// </para>
/// <para>
/// A plan must come out the same for the same content and arguments (<see cref="IOptiCliWriteTool{TArgs}"/>): the real
/// run plans again and is refused if the plan differs from the one the dry run showed.
/// </para>
/// <para>
/// Mistakes in the plan (an empty property name, content updated twice, a step after the content was trashed) throw
/// here, as faults of the site: the assistant gets an internal error, and the site's log the details.
/// </para>
/// </remarks>
public sealed class ContentPlan
{
    private readonly List<PlanStep> _steps = [];

    /// <summary>Marks a <see cref="PlannedContent"/> in a property value; never shown, so a value can't pass for one.</summary>
    internal string Nonce { get; } = Guid.NewGuid().ToString("N");

    /// <summary>How many steps the plan has.</summary>
    public int Count => _steps.Count;

    internal IReadOnlyList<PlanStep> Steps => _steps;

    /// <summary>
    /// A new draft of <paramref name="content"/>, in its language, with the changes <paramref name="change"/> makes, as
    /// <c>update_content</c> saves one. It is based on the version passed: plan from the latest version
    /// (<see cref="IOptiCliEditor.Load{T}"/> with <c>latest: true</c>), or the step is refused when someone saved a newer
    /// one.
    /// </summary>
    /// <exception cref="InvalidOperationException">An earlier step updates the same content in the same language, or trashes it.</exception>
    public ContentPlan Update(IContent content, Action<ContentChanges> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var link = Existing(content, nameof(content));
        var language = (content as ILocalizable)?.Language is { } culture && !CultureInfo.InvariantCulture.Equals(culture) ? culture.Name : null;
        NotTrashed(link);
        if (_steps.OfType<UpdateStep>().Any(u => u.Content.ID == link.ID && string.Equals(u.Language, language, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The plan updates content {link.ID}{(language is null ? "" : $" in '{language}'")} twice: put all its changes in one Update.");
        }
        var changes = new ContentChanges(this, creating: false);
        change(changes);
        Add(new UpdateStep(link, language, changes.NewName, changes.Properties, changes.AreaOps) { Number = _steps.Count + 1 });
        return this;
    }

    /// <summary>
    /// New content of <paramref name="type"/> below <paramref name="parent"/>, saved as a draft, as <c>create_content</c>
    /// makes it.
    /// </summary>
    /// <param name="type">The content type's name, e.g. <c>ArticlePage</c>.</param>
    /// <param name="values">Its property values (<see cref="ContentChanges.Set"/>).</param>
    /// <param name="lang">Its language, e.g. <c>en</c>; the parent's master language when left out.</param>
    /// <returns>The content to be, for later steps.</returns>
    public PlannedContent Create(IContent parent, string type, string name, Action<ContentChanges>? values = null, string? lang = null)
    {
        var link = Existing(parent, nameof(parent)).ToReferenceWithoutVersion();
        NotTrashed(link);
        return AddCreate(new PlanTarget(link, null), inAssets: false, type, name, values, lang);
    }

    /// <summary>New content below content an earlier step creates (<paramref name="parent"/>), saved as a draft.</summary>
    /// <inheritdoc cref="Create(IContent, string, string, Action{ContentChanges}?, string?)"/>
    public PlannedContent Create(PlannedContent parent, string type, string name, Action<ContentChanges>? values = null, string? lang = null) =>
        AddCreate(new PlanTarget(null, Planned(parent, nameof(parent))), inAssets: false, type, name, values, lang);

    /// <summary>
    /// New content in the "For this page" folder of <paramref name="content"/>, as blocks used only there are, saved as a
    /// draft (<c>create_content</c>'s <c>forContent</c>). The folder is made when the step runs, if it isn't there yet.
    /// </summary>
    /// <inheritdoc cref="Create(IContent, string, string, Action{ContentChanges}?, string?)"/>
    public PlannedContent CreateFor(IContent content, string type, string name, Action<ContentChanges>? values = null, string? lang = null)
    {
        var link = Existing(content, nameof(content)).ToReferenceWithoutVersion();
        NotTrashed(link);
        return AddCreate(new PlanTarget(link, null), inAssets: true, type, name, values, lang);
    }

    /// <summary>Moves <paramref name="content"/>, with everything below it, below <paramref name="destination"/>, as <c>move_content</c> does.</summary>
    /// <exception cref="InvalidOperationException">An earlier step trashes one of them.</exception>
    public ContentPlan Move(IContent content, IContent destination)
    {
        var link = Existing(content, nameof(content)).ToReferenceWithoutVersion();
        var to = Existing(destination, nameof(destination)).ToReferenceWithoutVersion();
        NotTrashed(link);
        NotTrashed(to);
        Add(new MoveStep(link, new PlanTarget(to, null)) { Number = _steps.Count + 1 });
        return this;
    }

    /// <summary>Moves <paramref name="content"/>, with everything below it, below content an earlier step creates.</summary>
    /// <inheritdoc cref="Move(IContent, IContent)"/>
    public ContentPlan Move(IContent content, PlannedContent destination)
    {
        var link = Existing(content, nameof(content)).ToReferenceWithoutVersion();
        var to = Planned(destination, nameof(destination));
        NotTrashed(link);
        Add(new MoveStep(link, new PlanTarget(null, to)) { Number = _steps.Count + 1 });
        return this;
    }

    /// <summary>
    /// Moves <paramref name="content"/>, with everything below it, to the CMS recycle bin, where an editor can restore it,
    /// as <c>delete_content</c> does. The tool is offered only where the site allows deleting.
    /// </summary>
    /// <exception cref="InvalidOperationException">An earlier step trashes it already.</exception>
    public ContentPlan Trash(IContent content)
    {
        var link = Existing(content, nameof(content)).ToReferenceWithoutVersion();
        NotTrashed(link);
        Add(new TrashStep(link) { Number = _steps.Count + 1 });
        return this;
    }

    private PlannedContent AddCreate(PlanTarget parent, bool inAssets, string type, string name, Action<ContentChanges>? values, string? lang)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var changes = new ContentChanges(this, creating: true);
        values?.Invoke(changes);
        var handle = new PlannedContent(this, _steps.Count + 1, type.Trim());
        Add(new CreateStep(handle, parent, inAssets, type.Trim(), name, string.IsNullOrWhiteSpace(lang) ? null : lang.Trim(), changes.Properties) { Number = handle.Step });
        return handle;
    }

    private void Add(PlanStep step) => _steps.Add(step);

    private static ContentReference Existing(IContent content, string parameter)
    {
        ArgumentNullException.ThrowIfNull(content, parameter);
        return ContentReference.IsNullOrEmpty(content.ContentLink)
            ? throw new ArgumentException("The content has no reference: plan with content that exists, or a PlannedContent for content the plan creates.", parameter)
            : content.ContentLink;
    }

    /// <exception cref="ArgumentException">A handle of another plan.</exception>
    internal PlannedContent Planned(PlannedContent planned, string parameter)
    {
        ArgumentNullException.ThrowIfNull(planned, parameter);
        return ReferenceEquals(planned.Plan, this)
            ? planned
            : throw new ArgumentException("The PlannedContent belongs to another plan: use the one this plan's Create returned.", parameter);
    }

    private void NotTrashed(ContentReference link)
    {
        if (_steps.OfType<TrashStep>().FirstOrDefault(t => t.Content.ID == link.ID) is { } trashed)
        {
            throw new InvalidOperationException($"Step {trashed.Number} of the plan trashes content {link.ID}: no later step can use it.");
        }
    }
}

/// <summary>
/// Content a <see cref="ContentPlan"/> creates, before it exists: later steps of the same plan use it as a parent
/// (<see cref="ContentPlan.Create(PlannedContent, string, string, Action{ContentChanges}?, string?)"/>), a destination
/// (<see cref="ContentPlan.Move(IContent, PlannedContent)"/>), or a reference in a property value
/// (<see cref="ContentChanges.Set"/>, <see cref="ContentChanges.AddToArea(string, PlannedContent, int?, string?)"/>). When the
/// plan runs, the module puts the new content's reference in its place.
/// </summary>
public sealed class PlannedContent
{
    internal PlannedContent(ContentPlan plan, int step, string type)
    {
        Plan = plan;
        Step = step;
        Type = type;
    }

    internal ContentPlan Plan { get; }

    /// <summary>The step that creates it, from 1.</summary>
    internal int Step { get; }

    /// <summary>Its content type's name.</summary>
    internal string Type { get; }

    /// <summary>Where it stands in a property value until the step that creates it has run (<see cref="ContentPlan.Nonce"/>).</summary>
    internal string Marker => PlanValues.Marker(Plan.Nonce, Step);

    /// <summary>The step that creates it, e.g. <c>step 2</c>.</summary>
    public override string ToString() => $"step {Step}";
}

/// <summary>
/// The changes of one step of a <see cref="ContentPlan"/>: property values, and for an update its name and ContentArea
/// edits.
/// </summary>
public sealed class ContentChanges
{
    private readonly ContentPlan _plan;
    private readonly bool _creating;

    internal ContentChanges(ContentPlan plan, bool creating)
    {
        _plan = plan;
        _creating = creating;
    }

    internal JsonObject Properties { get; } = [];

    internal string? NewName { get; private set; }

    internal List<PlanAreaOp> AreaOps { get; } = [];

    /// <summary>
    /// Sets <paramref name="property"/> to <paramref name="value"/>, in the forms <c>update_content</c> takes: a string
    /// (rich text as XHTML), a number, a boolean, a date; a content reference (<see cref="ContentReference"/>, an
    /// <see cref="IContent"/>, or a <see cref="PlannedContent"/>); a ContentArea as a list of items, e.g.
    /// <c>new[] { new { @ref = block }, new { @ref = plannedBlock, displayOption = "narrow" } }</c>; a list of
    /// references; a link as <c>new { href = "https://…", text = "…" }</c>; or the JSON itself
    /// (<see cref="JsonElement"/>, <see cref="JsonNode"/>). <c>null</c> clears it. Objects are written with camelCase
    /// names, dictionaries with their keys as they are. Setting a property twice keeps the last value.
    /// </summary>
    /// <exception cref="ArgumentException">An empty name, or a value of the CMS's own types other than a reference, rich text or a URL (a <c>ContentArea</c>, a block): give those in the forms above.</exception>
    public ContentChanges Set(string property, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        Properties[property.Trim()] = PlanValues.ToJson(value, _plan);
        return this;
    }

    /// <summary>Gives the content a new name (an update; a new item's name is <c>Create</c>'s).</summary>
    /// <exception cref="InvalidOperationException">A step that creates content.</exception>
    public ContentChanges SetName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Updating(nameof(SetName));
        NewName = name;
        return this;
    }

    /// <summary>
    /// Adds <paramref name="block"/> (a shared block, or a page) to the ContentArea <paramref name="property"/>, as
    /// <c>update_content</c>'s <c>areaOps</c> add does, after the property values are set.
    /// </summary>
    /// <param name="at">Its position, from 0; the end when left out.</param>
    /// <param name="displayOption">A display option the site defines, e.g. <c>narrow</c>.</param>
    /// <param name="ifMissing">Only when the area doesn't have it already.</param>
    /// <exception cref="InvalidOperationException">A step that creates content: give a new item's areas with <see cref="Set"/>.</exception>
    public ContentChanges AddToArea(string property, ContentReference block, int? at = null, string? displayOption = null, bool ifMissing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        if (ContentReference.IsNullOrEmpty(block))
        {
            throw new ArgumentException("An empty reference.", nameof(block));
        }
        Updating(nameof(AddToArea));
        AreaOps.Add(new PlanAreaOp(Protocol.AreaOps.Add, property.Trim(), new PlanTarget(block, null), at, displayOption, ifMissing));
        return this;
    }

    /// <summary>Adds content an earlier step creates (<paramref name="block"/>) to the ContentArea <paramref name="property"/>.</summary>
    /// <inheritdoc cref="AddToArea(string, ContentReference, int?, string?, bool)"/>
    public ContentChanges AddToArea(string property, PlannedContent block, int? at = null, string? displayOption = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        _plan.Planned(block, nameof(block));
        Updating(nameof(AddToArea));
        AreaOps.Add(new PlanAreaOp(Protocol.AreaOps.Add, property.Trim(), new PlanTarget(null, block), at, displayOption, IfMissing: false));
        return this;
    }

    /// <summary>Removes the first item of the ContentArea <paramref name="property"/> that refers to <paramref name="block"/>.</summary>
    /// <exception cref="InvalidOperationException">A step that creates content.</exception>
    public ContentChanges RemoveFromArea(string property, ContentReference block)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        if (ContentReference.IsNullOrEmpty(block))
        {
            throw new ArgumentException("An empty reference.", nameof(block));
        }
        Updating(nameof(RemoveFromArea));
        AreaOps.Add(new PlanAreaOp(Protocol.AreaOps.Remove, property.Trim(), new PlanTarget(block, null), null, null, false));
        return this;
    }

    private void Updating(string method)
    {
        if (_creating)
        {
            throw new InvalidOperationException($"{method} is for an update; a step that creates content takes its values from Set.");
        }
    }
}
