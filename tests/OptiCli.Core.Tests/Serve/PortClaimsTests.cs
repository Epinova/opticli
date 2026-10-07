using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

/// <summary>Two serves starting at the same moment don't pick the same free port, and claims never touch other files.</summary>
public class PortClaimsTests : IDisposable
{
    private readonly TempDirectory _root = new();

    private readonly List<PortClaims> _claims = [];

    public void Dispose()
    {
        foreach (var claims in _claims)
        {
            claims.ReleaseAll();
        }
        _root.Dispose();
    }

    private string Folder => Path.Combine(_root.Path, "ports");

    /// <summary>One "process": its own claims (each holds its own locks, as another process would).</summary>
    private PortClaims Process(string? folder = null)
    {
        var claims = new PortClaims(folder ?? Folder);
        _claims.Add(claims);
        return claims;
    }

    [Fact]
    public void A_port_another_process_holds_is_taken_and_select_moves_on()
    {
        var first = Process();
        var second = Process();

        Assert.True(first.TryClaim(5199));
        Assert.False(second.TryClaim(5199));
        // The same process may claim it again (a second probe).
        Assert.True(first.TryClaim(5199));
        Assert.Equal(5200, PortSelector.Select(null, null, second.FreeAndClaimed(_ => true)));
    }

    [Fact]
    public void A_released_claim_is_free_again_and_the_file_stays_untouched()
    {
        var first = Process();
        var second = Process();
        Assert.True(first.TryClaim(5199));

        first.ReleaseAll();

        Assert.True(second.TryClaim(5199));
        Assert.Equal(0, new FileInfo(Path.Combine(Folder, "5199")).Length);
    }

    [Fact]
    public void An_existing_file_is_locked_but_never_written_or_deleted()
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "5199");
        File.WriteAllText(file, "someone else's");

        var holder = Process();
        Assert.True(holder.TryClaim(5199));
        Assert.False(Process().TryClaim(5199));
        holder.ReleaseAll();

        Assert.Equal("someone else's", File.ReadAllText(file));
    }

    [Fact]
    public void The_probe_claims_only_ports_that_are_free()
    {
        var probe = Process().FreeAndClaimed(port => port != 5199);

        Assert.False(probe(5199));
        Assert.True(probe(5200));
        Assert.False(File.Exists(Path.Combine(Folder, "5199")));
        Assert.True(File.Exists(Path.Combine(Folder, "5200")));
    }

    [Fact]
    public void A_folder_that_cant_be_used_claims_nothing_and_blocks_nothing()
    {
        var file = Path.Combine(_root.Path, "not-a-folder");
        File.WriteAllText(file, "x");

        Assert.True(Process(file).TryClaim(5199));
        Assert.True(Process(file).TryClaim(5199));
        Assert.Equal("x", File.ReadAllText(file));
    }

    [Fact]
    public void A_symlinked_folder_or_claim_file_is_not_used()
    {
        var elsewhere = Path.Combine(_root.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var linked = Path.Combine(_root.Path, "linked");
        try
        {
            Directory.CreateSymbolicLink(linked, elsewhere);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            return; // Windows without the right to make symbolic links.
        }

        Assert.True(Process(linked).TryClaim(5199));
        Assert.True(Process(linked).TryClaim(5199));
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));

        Directory.CreateDirectory(Folder);
        var target = Path.Combine(_root.Path, "target.txt");
        File.WriteAllText(target, "keep");
        File.CreateSymbolicLink(Path.Combine(Folder, "5199"), target);
        Assert.True(Process().TryClaim(5199));
        Assert.True(Process().TryClaim(5199));
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Fact]
    public void On_unix_the_folder_is_private()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Directory.CreateDirectory(Folder);
        File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        Assert.True(Process().TryClaim(5199));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Folder));
    }

    [Fact]
    public void The_users_folder_is_the_runtime_dir_or_the_home_state_folder_never_the_shared_tmp()
    {
        var folder = PortClaims.UserDirectory();

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(Path.GetTempPath(), folder);
            return;
        }
        Assert.False(folder.StartsWith("/tmp/", StringComparison.Ordinal), folder);
        Assert.True(folder.EndsWith("opticli-ports", StringComparison.Ordinal) || folder.EndsWith(Path.Combine(".local", "state", "opticli", "ports"), StringComparison.Ordinal), folder);
    }
}
