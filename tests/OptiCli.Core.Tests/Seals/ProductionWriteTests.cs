using Microsoft.Data.SqlClient;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;
using OptiCli.Core.Seals;

namespace OptiCli.Core.Tests.Seals;

/// <summary>
/// The one way past the refusal to write to production: a verified seal, the database's name typed, and the write login
/// typed in place of the saved one's user and password (or nothing, when the saved connection signs in as the user).
/// </summary>
public sealed class ProductionWriteTests : IDisposable
{
    private const string ReadOnly = "Server=sql.example.net;Database=cms-prod;User ID=reader;Password=read-secret;Encrypt=True";

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private async Task<VerifiedSeal> VerifiedAsync()
    {
        var path = _folder.Write("plan.json", """{"operations": [{"op": "set", "ref": "61104228-962d-4dd2-925c-b5b12e2373ee", "properties": {"TeaserText": "x"}}]}""");
        var plan = await PlanSnapshot.ReadAsync(path, CancellationToken.None);
        var seal = SealFixtures.Seal(planHash: plan.Hash) with { Plan = path };
        return SealCheck.Verify(seal, SealFixtures.Target, SealFixtures.Project, plan, SealFixtures.Build, DateTimeOffset.UtcNow);
    }

    private static VerifiedConnectionString Production(string connection = ReadOnly, bool production = true) =>
        ConnectionSafety.Approve(connection, production: production);

    [Fact]
    public async Task The_typed_login_replaces_the_saved_one_for_writing_and_is_never_shown()
    {
        var write = ProductionWrite.Authorize(await VerifiedAsync(), Production(), " CMS-PROD ", new WriteCredentials(" writer ", "write-secret"));

        var builder = new SqlConnectionStringBuilder(write.Value);
        Assert.Equal(("writer", "write-secret", ApplicationIntent.ReadWrite, "sql.example.net", "cms-prod"), (builder.UserID, builder.Password, builder.ApplicationIntent, builder.DataSource, builder.InitialCatalog));
        Assert.DoesNotContain("secret", write.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", new WriteCredentials("writer", "write-secret").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", write.ForReads().ToString(), StringComparison.Ordinal);
        // Still production for everything else: nothing that writes or starts the site takes it.
        Assert.True(write.ForReads().IsProduction);
        Assert.Throws<RefusedException>(() => ProductionDatabase.RequireNot(write.ForReads(), "write to"));
    }

    [Fact]
    public async Task Without_the_database_name_typed_nothing_is_authorized()
    {
        var seal = await VerifiedAsync();

        Assert.Throws<UsageException>(() => ProductionWrite.Authorize(seal, Production(), null, new WriteCredentials("writer", "x")));
        Assert.Throws<UsageException>(() => ProductionWrite.Authorize(seal, Production(), "cms-pro", new WriteCredentials("writer", "x")));
        Assert.Throws<UsageException>(() => ProductionWrite.Authorize(seal, Production(), "y", new WriteCredentials("writer", "x")));
    }

    [Fact]
    public async Task Only_the_production_database_the_seal_is_for_without_a_failover_partner()
    {
        var seal = await VerifiedAsync();
        var credentials = new WriteCredentials("writer", "x");

        Assert.Throws<RefusedException>(() => ProductionWrite.Authorize(seal, Production(production: false), "cms-prod", credentials));
        Assert.Throws<RefusedException>(() => ProductionWrite.Authorize(seal, Production(ReadOnly.Replace("cms-prod", "cms-stage", StringComparison.Ordinal)), "cms-stage", credentials));
        Assert.Throws<RefusedException>(() => ProductionWrite.Authorize(seal, Production(ReadOnly + ";Failover Partner=sql2.example.net"), "cms-prod", credentials));
    }

    [Theory]
    [InlineData("Server=sql.example.net;Database=cms-prod;User ID=reader;Password=x", WriteLogin.Password)]
    [InlineData("Server=sql.example.net;Database=cms-prod;Authentication=SqlPassword;User ID=reader;Password=x", WriteLogin.Password)]
    [InlineData("Server=sql.example.net;Database=cms-prod;Authentication=Active Directory Service Principal;User ID=app;Password=x", WriteLogin.Password)]
    [InlineData("Server=sql.example.net;Database=cms-prod;Integrated Security=true", WriteLogin.Identity)]
    [InlineData("Server=sql.example.net;Database=cms-prod;Authentication=Active Directory Default", WriteLogin.Identity)]
    [InlineData("Server=sql.example.net;Database=cms-prod;Authentication=Active Directory Interactive", WriteLogin.Identity)]
    public void A_login_with_a_password_is_typed_and_the_users_own_identity_is_not(string connection, WriteLogin expected)
    {
        Assert.Equal(expected, ProductionWrite.LoginOf(Production(connection)));
    }

    [Fact]
    public async Task Credentials_go_with_a_password_login_only()
    {
        var seal = await VerifiedAsync();
        var identity = Production("Server=sql.example.net;Database=cms-prod;Authentication=Active Directory Default");

        Assert.Throws<UsageException>(() => ProductionWrite.Authorize(seal, Production(), "cms-prod", null));
        Assert.Throws<UsageException>(() => ProductionWrite.Authorize(seal, Production(), "cms-prod", new WriteCredentials("writer", "")));
        Assert.Throws<UsageException>(() => ProductionWrite.Authorize(seal, Production(), "cms-prod", new WriteCredentials(" ", "x")));
        Assert.Throws<UsageException>(() => ProductionWrite.Authorize(seal, identity, "cms-prod", new WriteCredentials("writer", "x")));
        var own = ProductionWrite.Authorize(seal, identity, "cms-prod", null);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryDefault, new SqlConnectionStringBuilder(own.Value).Authentication);
    }
}
