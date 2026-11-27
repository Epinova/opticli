using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Protocol;

namespace OptiCli.Mcp;

/// <summary>
/// What an action tool's call would do (<see cref="IOptiCliActionTool{TArgs}.PreviewAsync"/>): a target per thing it
/// changes, in the order the run changes them, and notes for the user. The dry run shows it to the user; the token covers
/// its targets (kind, id, label, effect, changes and version), not its notes and links.
/// </summary>
/// <example>
/// <code>
/// var preview = new ActionPreview();
/// var target = preview.Add("event", current.Id, current.Title, ActionEffects.Live)
///     .Version(current.SavedUtc.ToString("O"))
///     .Change("status", "draft", "published")
///     .Links(url: current.PublicUrl, editUrl: current.EditUrl);
/// preview.Note("Visitors see it on the calendar page within five minutes.");
/// </code>
/// </example>
public sealed class ActionPreview
{
    private readonly List<ActionTarget> _targets = [];
    private readonly List<string> _notes = [];

    /// <summary>The targets, in the order they were added: the order the run changes them.</summary>
    public IReadOnlyList<ActionTarget> Targets => _targets;

    /// <summary>The notes, in order.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Whether no target has a problem: the dry run is valid and gives a token.</summary>
    public bool Valid => _targets.All(t => t.Problems.Count == 0);

    /// <summary>
    /// Adds a thing the call changes. Describe what changes on the target it returns (<see cref="ActionTarget.Change"/>,
    /// <see cref="ActionTarget.Version"/>, <see cref="ActionTarget.Problem"/>, <see cref="ActionTarget.Links"/>).
    /// </summary>
    /// <param name="kind">What it is, as the user would say it: <c>event</c>, <c>offer</c>, <c>index</c>.</param>
    /// <param name="id">Its id in the system it lives in; null for one the run creates.</param>
    /// <param name="label">What the user knows it by: a title, a heading, a name. Empty for something with none yet.</param>
    /// <param name="effect">What changing it does to the site: one of <see cref="ActionEffects"/>, within the tool's <see cref="OptiCliActionToolAttribute.Effects"/>.</param>
    /// <exception cref="ArgumentException">No kind, or not exactly one effect.</exception>
    public ActionTarget Add(string kind, string? id, string label, ActionEffects effect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(label);
        if (effect is not (ActionEffects.Change or ActionEffects.Live or ActionEffects.Delete))
        {
            throw new ArgumentException($"A target has exactly one effect: Change, Live or Delete, not {effect}. Add a target per change of a different effect.", nameof(effect));
        }
        var target = new ActionTarget(this, _targets.Count + 1, kind.Trim(), string.IsNullOrWhiteSpace(id) ? null : id.Trim(), label.Trim(), effect);
        _targets.Add(target);
        return target;
    }

    /// <summary>
    /// Adds a line the user should see that isn't a change: what else the change reaches ("the 14 stores of this chain"),
    /// a rule that applies.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty.</exception>
    public ActionPreview Note(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _notes.Add(text.Trim());
        return this;
    }
}

/// <summary>
/// A thing an action tool's call changes (<see cref="ActionPreview.Add"/>): what it is, what changes on it, the version
/// the change is made from, what keeps it from running, and where the user sees it.
/// </summary>
public sealed class ActionTarget
{
    private readonly List<ActionChange> _changes = [];
    private readonly List<ActionProblem> _problems = [];

    internal ActionTarget(ActionPreview preview, int number, string kind, string? id, string label, ActionEffects effect)
    {
        Preview = preview;
        Number = number;
        Kind = kind;
        Id = id;
        Label = label;
        Effect = effect;
    }

    internal ActionPreview Preview { get; }

    /// <summary>Its place in the preview, from 1.</summary>
    public int Number { get; }

    /// <summary>What it is: <c>event</c>, <c>offer</c>.</summary>
    public string Kind { get; }

    /// <summary>Its id; null for one the run creates.</summary>
    public string? Id { get; }

    /// <summary>What the user knows it by.</summary>
    public string Label { get; }

    /// <summary>What changing it does to the site.</summary>
    public ActionEffects Effect { get; }

    /// <summary>The version the change is made from (<see cref="Version"/>); null when the system has none.</summary>
    public string? CurrentVersion { get; private set; }

    /// <summary>The fields that change, in the order they were added.</summary>
    public IReadOnlyList<ActionChange> Changes => _changes;

    /// <summary>What keeps it from running.</summary>
    public IReadOnlyList<ActionProblem> Problems => _problems;

    /// <summary>Its public address, once the run has made it live; null for none.</summary>
    public string? Url { get; private set; }

    /// <summary>Where the user reviews or edits it; null for none.</summary>
    public string? EditUrl { get; private set; }

    /// <summary>
    /// A field that changes, from <paramref name="before"/> (null for a new target, or a field without a value) to
    /// <paramref name="after"/>. Only fields that change: the token covers them, and the user sees them.
    /// </summary>
    /// <param name="before">A value System.Text.Json can write: a string, number, boolean, date, list or record.</param>
    /// <param name="after">The same.</param>
    /// <exception cref="ArgumentException">No field name, or the same field twice.</exception>
    /// <exception cref="NotSupportedException">A value System.Text.Json can't write.</exception>
    public ActionTarget Change(string field, object? before, object? after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (_changes.Any(c => c.Field == field))
        {
            throw new ArgumentException($"Target {Number} ({Kind}) already changes '{field}'.", nameof(field));
        }
        _changes.Add(new ActionChange(field, Json(before), Json(after)));
        return this;
    }

    /// <summary>
    /// The version the change is made from, as the system keeps it: a version number, a saved time, an ETag. The token
    /// covers it, so the run is refused when the target changed after the dry run. Set it wherever the system has one;
    /// without it only the <c>before</c> values of <see cref="Change"/> show a change made meanwhile.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="version"/> is empty.</exception>
    public ActionTarget Version(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        CurrentVersion = version;
        return this;
    }

    /// <summary>
    /// Something that keeps the change from running: a validator's error, a name that is taken. A preview with a problem
    /// gives no token; the assistant tells the user, and a new dry run after a fix may.
    /// </summary>
    /// <param name="message">What is wrong, for the user.</param>
    /// <param name="field">The field it is about; null for the target as a whole.</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty.</exception>
    public ActionTarget Problem(string message, string? field = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _problems.Add(new ActionProblem(message.Trim(), string.IsNullOrWhiteSpace(field) ? null : field));
        return this;
    }

    /// <summary>Where the user sees it: its public address, and where they review or edit it.</summary>
    public ActionTarget Links(string? url = null, string? editUrl = null)
    {
        Url = string.IsNullOrWhiteSpace(url) ? null : url;
        EditUrl = string.IsNullOrWhiteSpace(editUrl) ? null : editUrl;
        return this;
    }

    private static JsonNode? Json(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), AgentJson.Options);
}

/// <summary>A field of an <see cref="ActionTarget"/> that changes (<see cref="ActionTarget.Change"/>).</summary>
/// <param name="Field">The field.</param>
/// <param name="Before">Its value now, as JSON; null for none.</param>
/// <param name="After">Its value after the run, as JSON; null for none.</param>
public sealed record ActionChange(string Field, JsonNode? Before, JsonNode? After);

/// <summary>Something that keeps an <see cref="ActionTarget"/> from running (<see cref="ActionTarget.Problem"/>).</summary>
/// <param name="Message">What is wrong.</param>
/// <param name="Field">The field it is about; null for the target as a whole.</param>
public sealed record ActionProblem(string Message, string? Field);

/// <summary>
/// The real run of an action tool's call (<see cref="IOptiCliActionTool{TArgs}.RunAsync"/>): where the tool reports each
/// target as it is done.
/// </summary>
public sealed class ActionRun
{
    private readonly ActionPreview _preview;
    private readonly Action<ActionTarget, ActionRunOutcome> _done;
    private readonly Dictionary<ActionTarget, ActionRunOutcome> _outcomes = new(ReferenceEqualityComparer.Instance);
    private bool _ended;

    internal ActionRun(ActionPreview preview, string key, Action<ActionTarget, ActionRunOutcome> done)
    {
        _preview = preview;
        Key = key;
        _done = done;
    }

    /// <summary>
    /// The same for every call with this run's token, and for no other run: pass it to a system that takes a key to
    /// ignore a request it has already handled (an idempotency key), so a run that is cut off and made again can't make
    /// its changes twice. 64 hexadecimal characters.
    /// </summary>
    public string Key { get; }

    /// <summary>The targets reported done. Read once the run has ended (<see cref="End"/>), when nothing changes them any more.</summary>
    internal IReadOnlyDictionary<ActionTarget, ActionRunOutcome> Outcomes => _outcomes;

    /// <summary>Ends the run, when <c>RunAsync</c> returned or threw: a target reported after it is the site's fault.</summary>
    internal void End()
    {
        lock (_outcomes)
        {
            _ended = true;
        }
    }

    /// <summary>
    /// Reports <paramref name="target"/> done, as soon as it is: the module audits it at once, and if the run stops
    /// later, the result still says it was done. Safe to call from several threads.
    /// </summary>
    /// <param name="outcome">What was done, in a word or two: <c>saved</c>, <c>published</c>, <c>unpublished</c>, <c>deleted</c>, <c>unchanged</c>.</param>
    /// <param name="id">Its id, where the run gave it one (a target it created); null keeps the preview's.</param>
    /// <param name="url">Its public address now; null keeps the preview's.</param>
    /// <param name="editUrl">Where the user reviews or edits it now; null keeps the preview's.</param>
    /// <exception cref="ArgumentException">A target of another preview, or no outcome.</exception>
    /// <exception cref="InvalidOperationException">The target was reported before, or after <c>RunAsync</c> returned or threw.</exception>
    public void Done(ActionTarget target, string outcome, string? id = null, string? url = null, string? editUrl = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        if (!ReferenceEquals(target.Preview, _preview))
        {
            throw new ArgumentException($"Target {target.Number} ({target.Kind}) isn't one of this run's preview: report the targets of the preview RunAsync was given.", nameof(target));
        }
        var done = new ActionRunOutcome(
            outcome.Trim(),
            string.IsNullOrWhiteSpace(id) ? target.Id : id.Trim(),
            string.IsNullOrWhiteSpace(url) ? target.Url : url,
            string.IsNullOrWhiteSpace(editUrl) ? target.EditUrl : editUrl);
        lock (_outcomes)
        {
            if (_ended)
            {
                throw new InvalidOperationException($"Target {target.Number} ({target.Kind}) was reported after the run ended: report each target before RunAsync returns or throws.");
            }
            if (!_outcomes.TryAdd(target, done))
            {
                throw new InvalidOperationException($"Target {target.Number} ({target.Kind}) was reported done already.");
            }
        }
        _done(target, done);
    }
}

/// <summary>A target reported done (<see cref="ActionRun.Done"/>).</summary>
internal sealed record ActionRunOutcome(string Outcome, string? Id, string? Url, string? EditUrl);
