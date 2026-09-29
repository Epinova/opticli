using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Serve;

public class DatabaseMatchTests
{
    private static DatabaseTarget Target(string? server = "localhost,1433", string? name = "ExampleDb", bool local = true, bool pinned = true) =>
        new("EPiServerDB", server, name, local, pinned);

    [Fact]
    public void The_pinned_local_database_opticli_reads_matches()
    {
        Assert.Null(DatabaseMatch.Problem(Target(), "localhost,1433", "ExampleDb", expectedLocal: true, requirePinned: true));
        Assert.Null(DatabaseMatch.Problem(Target(server: "LOCALHOST,1433", name: "exampledb"), "localhost,1433", "ExampleDb", expectedLocal: true, requirePinned: true));
    }

    [Fact]
    public void A_different_database_on_the_same_server_is_a_problem()
    {
        var problem = DatabaseMatch.Problem(Target(name: "OtherDb"), "localhost,1433", "ExampleDb", expectedLocal: true, requirePinned: true);

        Assert.Contains("'OtherDb'", problem);
        Assert.Contains("'ExampleDb'", problem);
    }

    [Fact]
    public void A_different_server_is_a_problem()
    {
        Assert.NotNull(DatabaseMatch.Problem(Target(server: "localhost,1401"), "localhost,1433", "ExampleDb", expectedLocal: true, requirePinned: true));
    }

    [Fact]
    public void The_approved_remote_database_matches_when_opticli_reads_it_too()
    {
        Assert.Null(DatabaseMatch.Problem(Target(server: "tcp:dev.example.net,1433", local: false), "tcp:dev.example.net,1433", "ExampleDb", expectedLocal: false, requirePinned: true));
        Assert.NotNull(DatabaseMatch.Problem(Target(server: "tcp:prod.example.net,1433", local: false), "tcp:dev.example.net,1433", "ExampleDb", expectedLocal: false, requirePinned: true));
    }

    [Fact]
    public void A_non_local_database_is_a_problem_when_opticli_reads_a_local_one()
    {
        Assert.Contains("non-local", DatabaseMatch.Problem(Target(server: "db.example.com", local: false), "localhost,1433", "ExampleDb", expectedLocal: true, requirePinned: false));
    }

    [Fact]
    public void An_overridden_pin_is_a_problem_only_when_opticli_pinned_it()
    {
        Assert.Contains("replaced", DatabaseMatch.Problem(Target(pinned: false), "localhost,1433", "ExampleDb", expectedLocal: true, requirePinned: true));
        Assert.Null(DatabaseMatch.Problem(Target(pinned: false), "localhost,1433", "ExampleDb", expectedLocal: true, requirePinned: false));
    }
}
