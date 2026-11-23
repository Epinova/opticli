using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

/// <summary>Content is in one project at a time: a plan can't save it into two.</summary>
public class PlanProjectsTests
{
    private static readonly IReadOnlyList<ProjectInfo> Projects = [new(7, "Spring", "active", null, null, null, 0, 0)];

    private static void Check(string operations)
    {
        var plan = WritePlan.Parse($$"""{"operations": [{{operations}}]}""");
        PlanProjects.Of(plan.Steps, Projects).RequireOneProjectPerContent(plan.Steps);
    }

    [Fact]
    public void Two_steps_on_the_same_content_in_different_projects_are_refused_before_anything_is_saved()
    {
        var refused = Assert.Throws<UsageException>(() => Check("""
            {"op": "set", "ref": "123", "properties": {"A": 1}, "project": "Spring"},
            {"op": "set", "ref": "123_456", "properties": {"B": 2}, "project": "Autumn"}
            """));

        Assert.Contains("Operations 0 and 1 save 123_456 into different projects ('Spring' and 'Autumn')", refused.Message);

        // Planned content by its id, too.
        Assert.Throws<UsageException>(() => Check("""
            {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Page", "project": "Spring"},
            {"op": "set", "ref": "$page", "properties": {"B": 2}, "project": "Autumn"}
            """));
    }

    [Fact]
    public void The_same_project_by_id_or_name_another_language_or_other_content_are_fine()
    {
        Check("""
            {"op": "set", "ref": "123", "properties": {"A": 1}, "project": "Spring"},
            {"op": "set", "ref": "123", "properties": {"B": 2}, "project": "7"},
            {"op": "set", "ref": "123", "lang": "sv", "properties": {"C": 3}, "project": "Autumn"},
            {"op": "set", "ref": "124", "properties": {"D": 4}, "project": "Autumn"},
            {"op": "set", "ref": "123", "properties": {"E": 5}}
            """);
    }
}
