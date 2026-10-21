using System.Diagnostics;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Serve;

public class IdeProjectTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private IdeBlock Block(string? remote = null, string? name = null, string configuration = "Debug") =>
        new(_root.Combine("state/agent/1.0.0-abcd1234/cms12/OptiCli.Agent.dll"), _root.Combine("state/Web-123.ide.json"), configuration, remote, name);

    [Fact]
    public void A_new_file_gets_the_block_and_reads_back()
    {
        var file = _root.Combine("Web/Web.csproj.user");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var block = Block("tcp:dev.example.net,1433|ExampleDb", "CmsDb");

        Assert.True(IdeProject.Write(file, block));

        Assert.Equal(block, IdeProject.Read(file));
        var text = File.ReadAllText(file);
        Assert.Contains("Label=\"opticli ide\"", text, StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' == 'Debug'", text, StringComparison.Ordinal);
        Assert.Contains("'$(_IsPublishing)' != 'true'", text, StringComparison.Ordinal);
        Assert.Contains($"Exists('{block.AgentDll}')", text, StringComparison.Ordinal);
        Assert.Contains("Include=\"STARTUP_HOOKS\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_again_replaces_the_block_and_keeps_the_rest_of_the_file()
    {
        var file = _root.Write("Web/Web.csproj.user", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <ActiveDebugProfile>Web</ActiveDebugProfile>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(IdeProject.Write(file, Block()));
        Assert.False(IdeProject.Write(file, Block(configuration: "Local")));

        var text = File.ReadAllText(file);
        Assert.Contains("<ActiveDebugProfile>Web</ActiveDebugProfile>", text, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Label=\"opticli ide\""));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "opticli ide:"));
        Assert.StartsWith("<?xml", text, StringComparison.Ordinal);
        // The MSBuild namespace of the old format, on opticli's elements too: MSBuild ignores elements outside it.
        Assert.DoesNotContain("xmlns=\"\"", text, StringComparison.Ordinal);
        Assert.Equal("Local", IdeProject.Read(file)!.Configuration);
    }

    [Fact]
    public void Removing_deletes_a_file_with_nothing_else_and_keeps_one_with_other_settings()
    {
        var alone = _root.Combine("A/A.csproj.user");
        Directory.CreateDirectory(Path.GetDirectoryName(alone)!);
        IdeProject.Write(alone, Block());
        var shared = _root.Write("B/B.csproj.user", "<Project><PropertyGroup><ActiveDebugProfile>B</ActiveDebugProfile></PropertyGroup></Project>");
        IdeProject.Write(shared, Block());

        Assert.Equal((true, true), IdeProject.Remove(alone));
        Assert.False(File.Exists(alone));
        Assert.Equal((true, false), IdeProject.Remove(shared));
        Assert.Null(IdeProject.Read(shared));
        Assert.Contains("ActiveDebugProfile", File.ReadAllText(shared), StringComparison.Ordinal);
        Assert.DoesNotContain("opticli", File.ReadAllText(shared), StringComparison.Ordinal);
        Assert.Equal((false, false), IdeProject.Remove(shared));
    }

    [Fact]
    public void Ide_mode_is_on_for_a_project_folder_with_the_block()
    {
        var other = _root.Write("Web/Other.csproj.user", "<Project><PropertyGroup><ActiveDebugProfile>Web</ActiveDebugProfile></PropertyGroup></Project>");
        Assert.False(IdeProject.IsOn(_root.Combine("Web")));
        Assert.False(IdeProject.IsOn(_root.Combine("Missing")));

        IdeProject.Write(_root.Combine("Web/Web.csproj.user"), Block());

        Assert.True(IdeProject.IsOn(_root.Combine("Web")));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Characters_msbuild_treats_specially_in_paths_are_escaped_and_read_back()
    {
        var file = _root.Combine("Web/Web.csproj.user");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var block = Block() with { AgentDll = "/home/o'brien/50%;$x@y/OptiCli.Agent.dll" };

        IdeProject.Write(file, block);

        Assert.Contains("Exists('/home/o%27brien/50%25%3B%24x%40y/OptiCli.Agent.dll')", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(block, IdeProject.Read(file));
    }

    [Fact]
    public void A_user_file_that_isnt_xml_is_refused_rather_than_overwritten()
    {
        var file = _root.Write("Web/Web.csproj.user", "<Project><broken");

        Assert.Throws<UsageException>(() => IdeProject.Write(file, Block()));
        Assert.Equal("<Project><broken", File.ReadAllText(file));
    }

    [Fact]
    public void The_build_hook_is_read_from_the_runtimeconfig()
    {
        var dll = _root.Write("bin/Debug/net8.0/Web.dll", "");
        Assert.Equal((null, null), IdeProject.InBuild(dll));

        var agent = "/state/agent/1.0.0-abcd/cms12/OptiCli.Agent.dll";
        _root.Write("bin/Debug/net8.0/Web.runtimeconfig.json", $$"""
            {
              "runtimeOptions": {
                "tfm": "net8.0",
                "configProperties": {
                  "STARTUP_HOOKS": "/other/Hook.dll{{Path.PathSeparator}}{{agent}}",
                  "OptiCli.IdeFile": "/state/Web-123.ide.json"
                }
              }
            }
            """);

        Assert.Equal((agent, "/state/Web-123.ide.json"), IdeProject.InBuild(dll));
    }

    [Fact]
    public void A_copy_of_the_agent_is_made_once_per_build_and_old_ones_are_pruned()
    {
        var state = _root.Combine("state");
        var source = _root.Write("tool/agent/cms12/OptiCli.Agent.dll", "agent bytes v1");

        var copy = IdeProject.CopyAgent(state, source, 12);
        var again = IdeProject.CopyAgent(state, source, 12);
        File.WriteAllText(source, "agent bytes v2");
        var newer = IdeProject.CopyAgent(state, source, 12);

        Assert.Equal(copy, again);
        Assert.NotEqual(copy, newer);
        Assert.EndsWith(Path.Combine("cms12", "OptiCli.Agent.dll"), copy, StringComparison.Ordinal);
        Assert.Equal("agent bytes v1", File.ReadAllText(copy));

        var oldFolder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(copy)!, ".."));
        Directory.SetLastWriteTimeUtc(oldFolder, DateTime.UtcNow.AddDays(-40));
        Directory.SetLastWriteTimeUtc(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(newer)!, "..")), DateTime.UtcNow.AddDays(-40));

        var pruned = IdeProject.PruneCopies(state, newer, DateTime.UtcNow);

        Assert.Equal([oldFolder], pruned.Select(Path.GetFullPath));
        Assert.True(File.Exists(newer));
    }

    [Fact]
    public void Git_says_whether_the_file_would_be_committed_and_exclude_keeps_it_out()
    {
        if (!GitAvailable())
        {
            return;
        }
        var repository = _root.Combine("repo");
        Directory.CreateDirectory(Path.Combine(repository, "src", "Web"));
        RunGit(repository, "init", "-q");
        var file = Path.Combine(repository, "src", "Web", "Web.csproj.user");
        var outside = _root.Write("loose/Web.csproj.user", "<Project />");

        Assert.Equal(GitTracking.NoRepository, IdeProject.Tracking(outside));
        Assert.Equal(GitTracking.NotIgnored, IdeProject.Tracking(file));

        var exclude = IdeProject.Exclude(file);
        Assert.NotNull(exclude);
        Assert.Equal(GitTracking.Ignored, IdeProject.Tracking(file));
        Assert.Contains("/src/Web/Web.csproj.user", File.ReadAllLines(exclude));
        IdeProject.Exclude(file);
        Assert.Single(File.ReadAllLines(exclude), line => line == "/src/Web/Web.csproj.user");

        Assert.Equal(exclude, IdeProject.Unexclude(file));
        Assert.Equal(GitTracking.NotIgnored, IdeProject.Tracking(file));
        Assert.DoesNotContain(File.ReadAllLines(exclude), line => line.Contains("opticli", StringComparison.Ordinal));

        File.WriteAllText(file, "<Project />");
        RunGit(repository, "add", "-f", "src/Web/Web.csproj.user");
        Assert.Equal(GitTracking.Tracked, IdeProject.Tracking(file));
    }

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void RunGit(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void The_protocol_names_match_what_the_runtime_and_the_agent_read()
    {
        Assert.Equal("STARTUP_HOOKS", AgentProtocol.StartupHooksProperty);
        Assert.Equal("OptiCli.IdeFile", AgentProtocol.IdeFileProperty);
    }
}
