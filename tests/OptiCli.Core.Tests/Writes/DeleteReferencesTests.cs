using OptiCli.Core.Queries;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class DeleteReferencesTests
{
    private static IncomingReference Reference(int from) => new(from.ToString(System.Globalization.CultureInfo.InvariantCulture), "Owner", "ArticlePage", "en", "123", "MainArea", "contentArea");

    [Fact]
    public void Plans_confirm_a_delete_of_referenced_content_per_step()
    {
        var plan = WritePlan.Parse("""{"operations": [{"op": "delete", "ref": "123", "ignoreReferences": true}, {"op": "delete", "ref": "124"}]}""");

        Assert.Equal([true, false], plan.Steps.Select(s => ((DeleteOperation)s.Operation).IgnoreReferences));
    }

    [Fact]
    public void A_delete_output_lists_the_references_only_when_there_are_some()
    {
        var output = new MoveOutput("123", null, "ArticlePage", "News", "en", "published", null, "5", Moved: false, DryRun: true, Descendants: 0, RecycleBin: true);

        Assert.Same(output, output.WithReferences(new IncomingReferences([], 0)));
        var referenced = output.WithReferences(new IncomingReferences([Reference(40)], 51));
        Assert.Equal((1, 51), (referenced.References!.Count, referenced.ReferenceCount));
    }

    [Fact]
    public void The_description_names_the_first_few()
    {
        var found = new IncomingReferences(Enumerable.Range(1, 7).Select(Reference).ToList(), 7);

        Assert.Equal("7 reference(s) from other content (1 MainArea -> 123, 2 MainArea -> 123, 3 MainArea -> 123, 4 MainArea -> 123, 5 MainArea -> 123, ...)", found.Describe());
    }
}
