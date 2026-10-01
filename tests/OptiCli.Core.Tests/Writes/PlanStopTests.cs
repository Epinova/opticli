using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class PlanStopTests
{
    [Fact]
    public void Whatever_stops_a_running_plan_becomes_an_error_it_can_report_with()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var interrupted = PlanRunner.Stopped(new OperationCanceledException(), cancelled.Token);
        Assert.Equal((ErrorCode.Cancelled, 130), (interrupted.Code, ExitCodes.For(interrupted.Code)));
        Assert.Contains("opticli versions", interrupted.Hint);

        Assert.Equal(ErrorCode.NotFound, PlanRunner.Stopped(new FileNotFoundException("gone"), CancellationToken.None).Code);
        Assert.Equal(ErrorCode.Usage, PlanRunner.Stopped(new IOException("locked"), CancellationToken.None).Code);
        Assert.Equal(ErrorCode.Internal, PlanRunner.Stopped(new InvalidOperationException("bug"), CancellationToken.None).Code);

        var known = new ConflictException("newer version");
        Assert.Same(known, PlanRunner.Stopped(known, CancellationToken.None));
    }

    [Fact]
    public void Content_that_contains_a_protected_item_is_found_by_its_ancestors()
    {
        ContentHeader Header(int id, string path) => new(id, Guid.NewGuid(), 1, null, path, 1, false, 0, 0, new Dictionary<int, ContentLanguageRow>());
        ContentHeader?[] protectedContent = [Header(5, ".1."), Header(40, ".1.30.35."), null];

        Assert.Equal(40, WriteExecutor.ContainedProtected(35, protectedContent));
        Assert.Equal(40, WriteExecutor.ContainedProtected(30, protectedContent));
        Assert.Equal(5, WriteExecutor.ContainedProtected(1, protectedContent));
        Assert.Null(WriteExecutor.ContainedProtected(40, protectedContent));
        Assert.Null(WriteExecutor.ContainedProtected(36, protectedContent));
    }
}
