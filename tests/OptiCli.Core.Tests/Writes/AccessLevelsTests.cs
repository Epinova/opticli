using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

public class AccessLevelsTests
{
    [Theory]
    [InlineData("Read", 1)]
    [InlineData("read, edit", 5)]
    [InlineData("Read,Create,Edit,Delete,Publish,Administer", 63)]
    [InlineData("FullAccess", 63)]
    [InlineData("fullaccess,Read", 63)]
    [InlineData("Publish", 16)]
    public void Parses_level_lists(string text, int mask)
    {
        Assert.True(AccessLevels.TryParse(text, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(mask, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    [InlineData(null)]
    [InlineData("Raed")]
    [InlineData("Read,NoAccess")]
    public void Rejects_anything_else(string? text)
    {
        Assert.False(AccessLevels.TryParse(text, out _, out var error));
        Assert.Contains("Read, Create, Edit", error);
    }

    [Theory]
    [InlineData(63, new[] { "FullAccess" })]
    [InlineData(1, new[] { "Read" })]
    [InlineData(5, new[] { "Read", "Edit" })]
    [InlineData(0, new string[0])]
    public void Describes_masks(int mask, string[] names) => Assert.Equal(names, AccessLevels.Describe(mask));

    [Theory]
    [InlineData(1)]
    [InlineData(21)]
    [InlineData(63)]
    public void Format_round_trips(int mask)
    {
        Assert.True(AccessLevels.TryParse(AccessLevels.Format(mask), out var parsed, out _));
        Assert.Equal(mask, parsed);
    }

    [Theory]
    [InlineData(0, "user")]
    [InlineData(1, "role")]
    [InlineData(2, "visitorGroup")]
    public void Entry_kinds_come_from_the_security_entity_type(int type, string kind) => Assert.Equal(kind, AccessKinds.From(type));
}
