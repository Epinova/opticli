using System.Text;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;

namespace OptiCli.Core.Tests.Data;

public class LoginFailureTests
{
    private const string TokenPrincipal = "Login failed for user '<token-identified principal>'.";

    private static string Token(string claims) =>
        $"{Encode("""{"alg":"none"}""")}.{Encode(claims)}.";

    private static string Encode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void An_entra_user_without_a_database_user_is_named_by_upn_and_object_id()
    {
        var token = Token("""{"upn":"kari@example.com","oid":"11111111-2222-3333-4444-555555555555","tid":"tenant-1","idtyp":"user"}""");

        var ex = LoginFailure.Create(18456, TokenPrincipal, "dev.database.windows.net", "cms-dev", false,
            SqlAuthenticationMethod.ActiveDirectoryDefault, null, token);

        Assert.Equal(ErrorCode.LoginFailed, ex.Code);
        Assert.Equal(ExitCodes.Unreachable, ExitCodes.For(ex.Code));
        Assert.Equal("login_failed", ExitCodes.Name(ex.Code));
        Assert.Contains("refused the Microsoft Entra identity kari@example.com (object id 11111111-2222-3333-4444-555555555555) for database 'cms-dev'", ex.Message);
        Assert.Contains("signed in, but has no user in database 'cms-dev'", ex.Hint);
        Assert.Contains("CREATE USER [kari@example.com] FROM EXTERNAL PROVIDER", ex.Hint);
        Assert.Contains("`az login`", ex.Hint);
        Assert.DoesNotContain("firewall", ex.Hint);
        Assert.Equal(
            """{"identity":{"name":"kari@example.com","objectId":"11111111-2222-3333-4444-555555555555","tenantId":"tenant-1"}}""",
            JsonOutput.Serialize(ex.Details));
    }

    [Fact]
    public void A_service_principal_is_named_by_its_ids()
    {
        var token = Token("""{"appid":"aaaa-bbbb","oid":"cccc-dddd","tid":"t","idtyp":"app"}""");

        var ex = LoginFailure.Create(18456, TokenPrincipal, "dev.database.windows.net", "cms-dev", false,
            SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, "aaaa-bbbb", token);

        Assert.Contains("the Microsoft Entra identity object id cccc-dddd, application id aaaa-bbbb", ex.Message);
        Assert.Contains("CREATE USER [<name>]", ex.Hint);
        Assert.Contains("change the identity in the connection string", ex.Hint);
    }

    [Fact]
    public void Without_a_token_entra_isnt_said_to_have_signed_in()
    {
        var ex = LoginFailure.Create(18456, "Login failed for user ''.", "localhost", "cms", true,
            SqlAuthenticationMethod.ActiveDirectoryDefault, null, null);

        Assert.Equal(ErrorCode.LoginFailed, ex.Code);
        Assert.Contains("refused the Microsoft Entra login for database 'cms'", ex.Message);
        Assert.DoesNotContain("signed in", ex.Message + ex.Hint);
        Assert.Contains("may not take Microsoft Entra logins", ex.Hint);
        Assert.Null(ex.Details);
    }

    [Fact]
    public void A_sql_login_points_at_the_user_name_and_password()
    {
        var ex = LoginFailure.Create(18456, "Login failed for user 'sa'.", "localhost,1433", "cms", true,
            SqlAuthenticationMethod.NotSpecified, "sa", null);

        Assert.Contains("refused the login 'sa' for database 'cms'", ex.Message);
        Assert.Contains("user name and password", ex.Hint);
        Assert.Equal("""{"identity":{"name":"sa"}}""", JsonOutput.Serialize(ex.Details));
    }

    [Fact]
    public void A_database_the_login_cant_open_may_not_exist()
    {
        var ex = LoginFailure.Create(4060, "Cannot open database \"cms\" requested by the login. The login failed.", "localhost", "cms", true,
            SqlAuthenticationMethod.SqlPassword, "sa", null);

        Assert.Contains("Check that database 'cms' exists on 'localhost'", ex.Hint);
    }

    [Theory]
    [InlineData("not a token")]
    [InlineData("a.!!!.c")]
    [InlineData("a.e30.c")] // {}
    public void An_unreadable_token_names_nobody(string token)
    {
        Assert.Null(LoginIdentity.FromToken(token));
    }

    [Fact]
    public async Task The_capture_hands_on_the_wrapped_providers_token_and_keeps_it()
    {
        // Workload identity: no test connects with it, so swapping its provider can't affect another test.
        const SqlAuthenticationMethod method = SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity;
        var fake = new FakeProvider("header.payload.signature");
        Assert.True(SqlAuthenticationProvider.SetProvider(method, fake));

        EntraTokenCapture.Prepare(method);
        EntraTokenCapture.Prepare(method);

        var wrapped = SqlAuthenticationProvider.GetProvider(method);
        Assert.IsType<EntraTokenCapture>(wrapped);
        Assert.True(wrapped!.IsSupported(method));
        var token = await wrapped.AcquireTokenAsync(new SqlAuthenticationParameters(
            method, "server", "db", "https://database.windows.net/", "https://login.microsoftonline.com/t", null, null, Guid.NewGuid(), 15));
        Assert.Equal("header.payload.signature", token.AccessToken);
        Assert.Equal("header.payload.signature", EntraTokenCapture.LastToken);
        Assert.Equal(1, fake.Calls);
    }

    private sealed class FakeProvider(string token) : SqlAuthenticationProvider
    {
        public int Calls { get; private set; }

        public override bool IsSupported(SqlAuthenticationMethod authenticationMethod) => true;

        public override Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters)
        {
            Calls++;
            return Task.FromResult(new SqlAuthenticationToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
