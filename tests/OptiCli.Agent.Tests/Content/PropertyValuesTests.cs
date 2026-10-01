using System.Text.Json;
using OptiCli.Agent.Content;

namespace OptiCli.Agent.Tests.Content;

public class PropertyValuesTests
{
    [Fact]
    public void A_contentarea_before_says_explicitly_that_items_had_no_personalization()
    {
        var before = JsonSerializer.SerializeToElement(JsonDocument.Parse("""[{"ref":"12"},{"ref":"13","group":"g","visitorGroups":["a"]}]""").RootElement);

        var shown = PropertyValues.ExplicitPersonalization(before)!.Value.GetRawText();

        Assert.Equal("""[{"ref":"12","group":"","visitorGroups":[]},{"ref":"13","group":"g","visitorGroups":["a"]}]""", shown);
    }

    [Theory]
    [InlineData("""[{"href":"/x","text":"X"}]""")]
    [InlineData("""["12","13"]""")]
    [InlineData("\"text\"")]
    public void Other_values_are_left_as_they_are(string json)
    {
        var value = JsonSerializer.SerializeToElement(JsonDocument.Parse(json).RootElement);

        Assert.Equal(json, PropertyValues.ExplicitPersonalization(value)!.Value.GetRawText());
    }
}
