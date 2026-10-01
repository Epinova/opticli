using OptiCli.Core.Queries;

namespace OptiCli.Core.Tests.Queries;

public class ApprovalReaderTests
{
    private static string? Code(int id) => id switch { 1 => "en", 8 => "sv", _ => null };

    private static ApprovalReader.Row Reviewer(int owner, int step, string name, int type = 1, int language = 1, bool enabled = true) =>
        new(ApprovalReader.Key(owner), enabled, step, $"Step {step}", name, type, language);

    [Fact]
    public void The_nearest_definition_applies_and_says_whether_it_is_inherited()
    {
        ApprovalReader.Row[] rows = [Reviewer(200, 0, "Editors"), Reviewer(1, 0, "Root reviewers")];

        var own = ApprovalReader.Resolve(200, [200, 1], rows, Code);
        var inherited = ApprovalReader.Resolve(210, [210, 200, 1], rows, Code);

        Assert.Equal(("200", false), (own?.DefinedOn, own?.Inherited));
        Assert.Equal(("200", true), (inherited?.DefinedOn, inherited?.Inherited));
        Assert.Equal("Editors", Assert.Single(Assert.Single(inherited!.Steps).Reviewers).Name);
    }

    [Fact]
    public void A_disabled_definition_turns_approvals_off_below_it()
    {
        ApprovalReader.Row[] rows = [Reviewer(200, 0, "Editors", enabled: false), Reviewer(1, 0, "Root reviewers")];

        Assert.Null(ApprovalReader.Resolve(210, [210, 200, 1], rows, Code));
        Assert.Null(ApprovalReader.Resolve(5, [5], rows, Code));
    }

    [Fact]
    public void Reviewers_are_grouped_per_step_with_their_languages()
    {
        ApprovalReader.Row[] rows =
        [
            Reviewer(200, 1, "anna", type: 0, language: 8),
            Reviewer(200, 0, "Editors", language: 1),
            Reviewer(200, 0, "Editors", language: 8),
            Reviewer(200, 1, "anna", type: 0, language: 1),
        ];

        var sequence = ApprovalReader.Resolve(200, [200], rows, Code)!;

        Assert.Equal(["Step 0", "Step 1"], sequence.Steps.Select(s => s.Name));
        var editors = Assert.Single(sequence.Steps[0].Reviewers);
        Assert.Equal(("Editors", "role"), (editors.Name, editors.Kind));
        Assert.Equal(["en", "sv"], editors.Languages);
        Assert.Equal("user", Assert.Single(sequence.Steps[1].Reviewers).Kind);
        Assert.Equal("defined on 200, 2 step(s): Step 0 (role Editors), Step 1 (user anna)", sequence.Describe());
    }
}
