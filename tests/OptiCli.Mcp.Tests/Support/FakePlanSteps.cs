using System.Globalization;
using System.Text.Json.Nodes;
using EPiServer.Core;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp.Tests.Support;

/// <summary>
/// Stands in for the CMS behind a write tool's plan: its content's latest versions, and what each step's check and run
/// were given. A test makes a check refuse, find a step invalid, or a run fail.
/// </summary>
internal sealed class FakePlanSteps : IContentPlanSteps
{
    private int _next = 1000;

    /// <summary>The latest version of each content id; a saved update makes a new one.</summary>
    public Dictionary<int, int> Versions { get; } = new() { [5] = 10, [6] = 20, [7] = 30, [8] = 40 };

    public List<(string Phase, PlanStep Step)> Calls { get; } = [];

    public Func<PlanStep, Exception?> RefuseCheck { get; set; } = _ => null;

    public Func<PlanStep, bool> Invalid { get; set; } = _ => false;

    public Func<PlanStep, Exception?> FailRun { get; set; } = _ => null;

    public IEnumerable<PlanStep> Checked => Calls.Where(c => c.Phase == "check").Select(c => c.Step);

    public IEnumerable<PlanStep> Ran => Calls.Where(c => c.Phase == "run").Select(c => c.Step);

    /// <summary>Content as a tool loads it, with <paramref name="id"/>'s latest version.</summary>
    public TestContent Latest(int id, string? language = "en") => new(new ContentReference(id, Versions[id]), language);

    public StepOutcome Check(PlanStep step)
    {
        lock (Calls)
        {
            Calls.Add(("check", step));
        }
        if (RefuseCheck(step) is { } refusal)
        {
            throw refusal;
        }
        var report = new JsonObject();
        JsonObject? facts = null;
        switch (step)
        {
            case UpdateStep update:
                report["content"] = new JsonObject { ["ref"] = update.Content.ToString() };
                report["changes"] = update.Properties.DeepClone();
                facts = new JsonObject { ["baseVersion"] = update.Content.WorkID > 0 ? update.Content.WorkID : Versions[update.Content.ID] };
                break;
            case CreateStep create:
                report["checkedBelow"] = create.Parent.Existing!.ToString();
                report["changes"] = create.Properties.DeepClone();
                break;
            case MoveStep move:
                report["content"] = new JsonObject { ["ref"] = move.Content.ToString() };
                report["parent"] = move.Destination.Existing!.ToString();
                facts = new JsonObject { ["previousParent"] = "1" };
                break;
            case TrashStep trash:
                report["content"] = new JsonObject { ["ref"] = trash.Content.ToString() };
                report["descendants"] = 2;
                facts = new JsonObject { ["descendants"] = 2 };
                break;
        }
        report["dryRun"] = true;
        return new StepOutcome(report) { Valid = !Invalid(step), Facts = facts };
    }

    public StepOutcome Run(PlanStep step, JsonObject? facts)
    {
        lock (Calls)
        {
            Calls.Add(("run", step));
        }
        if (FailRun(step) is { } failure)
        {
            throw failure;
        }
        switch (step)
        {
            case UpdateStep update:
                var saved = new ContentReference(update.Content.ID, ++Versions[update.Content.ID]);
                return new StepOutcome(new JsonObject { ["content"] = new JsonObject { ["ref"] = saved.ToString() }, ["editUrl"] = $"http://localhost/edit/{saved}" })
                {
                    Status = "saved",
                    Content = saved,
                };
            case CreateStep:
                var made = new ContentReference(_next++, 1);
                Versions[made.ID] = 1;
                return new StepOutcome(new JsonObject { ["content"] = new JsonObject { ["ref"] = made.ToString() } }) { Status = "saved", Content = made };
            case MoveStep move:
                return new StepOutcome(new JsonObject { ["parent"] = move.Destination.Existing!.ToString() }) { Status = "moved", Content = move.Content };
            default:
                return new StepOutcome(new JsonObject()) { Status = "trashed", Content = ((TrashStep)step).Content };
        }
    }
}

/// <summary>Content as a tool loaded it: a reference (with its version) and a language. Nothing else is read of it.</summary>
public sealed class TestContent(ContentReference link, string? language = "en") : IContent, ILocalizable
{
    public string Name { get; set; } = "Test";

    public ContentReference ContentLink { get; set; } = link;

    public ContentReference ParentLink { get; set; } = ContentReference.RootPage;

    public Guid ContentGuid { get; set; } = Guid.NewGuid();

    public int ContentTypeID { get; set; } = 1;

    public bool IsDeleted { get; set; }

    public PropertyDataCollection Property { get; } = [];

    public CultureInfo Language { get; set; } = language is null ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language);

    public IEnumerable<CultureInfo> ExistingLanguages { get; set; } = [];

    public CultureInfo MasterLanguage { get; set; } = CultureInfo.GetCultureInfo("en");
}
