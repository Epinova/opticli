using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Serve;

public class AgentErrorsTests
{
    [Theory]
    [InlineData(AgentErrorCodes.Usage, ErrorCode.Usage, 1)]
    [InlineData(AgentErrorCodes.NotFound, ErrorCode.NotFound, 2)]
    [InlineData(AgentErrorCodes.UnsupportedProtocol, ErrorCode.NotFound, 2)]
    [InlineData(AgentErrorCodes.Unauthorized, ErrorCode.Refused, 3)]
    [InlineData(AgentErrorCodes.Refused, ErrorCode.Refused, 3)]
    [InlineData(AgentErrorCodes.Conflict, ErrorCode.Conflict, 5)]
    [InlineData(AgentErrorCodes.Validation, ErrorCode.Validation, 5)]
    [InlineData(AgentErrorCodes.Internal, ErrorCode.Internal, 1)]
    [InlineData("something_new", ErrorCode.Internal, 1)]
    public void Agent_codes_map_to_cli_codes_and_exit_codes(string agentCode, ErrorCode expected, int exit)
    {
        var exception = AgentErrors.ToException(new AgentError(agentCode, "message"));

        Assert.Equal(expected, exception.Code);
        Assert.Equal(exit, ExitCodes.For(exception.Code));
        Assert.Equal("message", exception.Message);
    }

    [Fact]
    public void Validation_issues_and_the_current_version_become_details()
    {
        var error = new AgentError(AgentErrorCodes.Validation, "Validation failed")
        {
            Validation = [new ValidationIssue("Heading", "Required")],
        };
        var details = Assert.IsType<AgentErrorDetails>(AgentErrors.ToException(error).Details);
        Assert.Equal("Heading", Assert.Single(details.Validation!).Property);

        var conflict = AgentErrors.ToException(new AgentError(AgentErrorCodes.Conflict, "Not latest") { CurrentVersion = 457 });
        Assert.Equal(457, Assert.IsType<AgentErrorDetails>(conflict.Details).CurrentVersion);
        Assert.Null(AgentErrors.ToException(new AgentError(AgentErrorCodes.Usage, "x")).Details);
    }

    [Fact]
    public void An_out_of_date_agent_says_how_to_restart_it()
    {
        Assert.Equal(AgentErrors.OutOfDateHint, AgentErrors.ToException(new AgentError(AgentErrorCodes.UnsupportedProtocol, "v2")).Hint);
    }

    [Fact]
    public void Parse_unwraps_data_and_throws_typed_errors()
    {
        Assert.Equal("x", AgentClient.Parse<MoveRequest>("""{"ok":true,"data":{"parent":"x"},"meta":{"source":"agent","version":"1.0.0","protocol":1}}""", 200).Parent);

        var conflict = Assert.Throws<ConflictException>(() => AgentClient.Parse<MoveRequest>(
            """{"ok":false,"error":{"code":"conflict","message":"Version 5 is not the latest","currentVersion":6},"meta":{"source":"agent","version":"1.0.0","protocol":1}}""", 409));
        Assert.Equal(6, Assert.IsType<AgentErrorDetails>(conflict.Details).CurrentVersion);
    }

    [Fact]
    public void Parse_reports_a_protocol_mismatch_as_an_out_of_date_agent()
    {
        var error = Assert.Throws<NotFoundException>(() => AgentClient.Parse<MoveRequest>(
            """{"ok":true,"data":{"parent":"x"},"meta":{"source":"agent","version":"9.0.0","protocol":99}}""", 200));
        Assert.Contains("out of date", error.Message);
    }

    [Theory]
    [InlineData("<html>Not found</html>")]
    [InlineData("""{"ok":true}""")]
    [InlineData("")]
    public void Parse_treats_anything_but_an_envelope_as_a_missing_agent(string body)
    {
        var error = Assert.Throws<AgentMissingException>(() => AgentClient.Parse<MoveRequest>(body, 404));
        Assert.Equal(ErrorCode.Unreachable, error.Code);
    }
}
