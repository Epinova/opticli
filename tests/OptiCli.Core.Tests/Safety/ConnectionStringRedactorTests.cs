using OptiCli.Core.Safety;

namespace OptiCli.Core.Tests.Safety;

public class ConnectionStringRedactorTests
{
    [Theory]
    [InlineData("Server=localhost;Database=Cms;User Id=app;Password=secret")]
    [InlineData("Server=localhost;Database=Cms;User Id=app;PWD=secret")]
    [InlineData("Server=localhost;Database=Cms;User Id=app;Password='se;cret'")]
    [InlineData("Server=localhost;Bogus=1;Password=secret")]
    public void Masks_passwords(string connectionString)
    {
        var redacted = ConnectionStringRedactor.Redact(connectionString);

        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("cret", redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_strings_without_password_alone()
    {
        var redacted = ConnectionStringRedactor.Redact("Server=localhost;Database=Cms;Integrated Security=True");

        Assert.DoesNotContain("***", redacted, StringComparison.Ordinal);
        Assert.Contains("localhost", redacted, StringComparison.Ordinal);
    }
}
