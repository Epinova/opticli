using System.Text.Json;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class IntoVersionTests
{
    [Fact]
    public void Set_area_and_composition_operations_keep_the_draft_to_save_into_through_refs_and_publish()
    {
        WriteOperation[] operations =
        [
            new SetOperation("$page", Name: "x") { IntoVersion = 458 },
            new AreaEdit("$page", "MainContentArea", "add", Item: "$block") { IntoVersion = 458 },
            new CompositionEdit("$page", "remove", Node: "hero") { IntoVersion = 458 },
        ];

        foreach (var operation in operations)
        {
            var mapped = operation.MapRefs(r => r.Replace("$page", "123", StringComparison.Ordinal)).WithPublish();
            Assert.Equal(458, mapped switch
            {
                SetOperation set => set.IntoVersion,
                AreaEdit area => area.IntoVersion,
                CompositionEdit composition => composition.IntoVersion,
                _ => null,
            });
        }
    }

    [Fact]
    public void It_is_no_plan_or_output_field()
    {
        // Internal: only a plan run sets it, so serializing an operation (a plan's echo, a result) never shows it.
        Assert.DoesNotContain("intoVersion", JsonSerializer.Serialize(new SetOperation("123", Name: "x") { IntoVersion = 458 }), StringComparison.OrdinalIgnoreCase);
    }
}
