using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Serve;

public class AgentClientTests
{
    [Theory]
    [InlineData(404, "<html><body><h2>HTTP Error 404.13 - Not Found</h2><p>The request filtering module is configured to deny a request that exceeds the request content length.</p></body></html>")]
    [InlineData(413, "Request Entity Too Large")]
    public void A_server_refusing_a_large_body_says_so_instead_of_blaming_the_agent(int status, string page)
    {
        var refused = Assert.Throws<UsageException>(() => AgentClient.Parse<WriteResult>(page, status));

        Assert.Contains("too large", refused.Message);
        Assert.Contains("maxAllowedContentLength", refused.Hint);
        Assert.Throws<AgentMissingException>(() => AgentClient.Parse<WriteResult>("<html>Not found</html>", 404));
    }

    [Fact]
    public void An_agent_that_rejects_a_field_this_cli_sends_is_out_of_date()
    {
        const string envelope = """
            {"ok":false,"error":{"code":"usage","message":"The request body is not valid: The JSON property 'requestApproval' could not be mapped to any .NET member contained in type 'OptiCli.Protocol.DraftRequest'."},"meta":{"source":"agent","version":"0.5.0","protocol":1}}
            """;

        var stale = Assert.Throws<UsageException>(() => AgentClient.Parse<WriteResult>(envelope, 400));

        Assert.Contains("0.5.0", stale.Message);
        Assert.Equal(AgentErrors.OutOfDateHint, stale.Hint);
    }
}
