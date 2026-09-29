using System.Reflection;
using OptiCli.Core.Errors;
using OptiCli.Core.Skills;

namespace OptiCli.Core.Tests.Skills;

public class SkillTests
{
    private const string Skill = """
        ---
        name: opticli
        description: Use it.
        opticli-version: 1.2.0
        ---
        # Body
        opticli-version: 9.9.9 is not front matter
        """;

    [Fact]
    public void Version_comes_from_the_front_matter_only()
    {
        Assert.Equal("1.2.0", SkillBundle.ReadVersion(Skill));
        Assert.Equal("1.2.0", SkillBundle.ReadVersion("﻿" + Skill.Replace("1.2.0", "\"1.2.0\"")));
        Assert.Null(SkillBundle.ReadVersion("# No front matter\nopticli-version: 1.0.0\n"));
        Assert.Null(SkillBundle.ReadVersion("---\nname: opticli\n---\n"));
    }

    [Theory]
    [InlineData("0.1.0", "0.2.0", -1)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("1.0.0", "1.0", 0)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0+abc", "1.0.0", 0)]
    public void Versions_compare_numerically(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(ToolVersions.Compare(a, b)!.Value));
    }

    [Fact]
    public void Unparseable_versions_do_not_compare()
    {
        Assert.Null(ToolVersions.Compare("latest", "1.0.0"));
        Assert.Null(ToolVersions.Compare(null, "1.0.0"));
    }

    [Fact]
    public void Install_writes_every_file_then_leaves_an_identical_copy_alone()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine(".claude/skills/opticli");
        var bundle = Bundle("1.2.0");

        var first = SkillInstaller.Install(bundle, target, force: false);
        var second = SkillInstaller.Install(bundle, target, force: false);

        Assert.Equal("installed", first.Status);
        Assert.Equal("unchanged", second.Status);
        Assert.Equal(Skill, File.ReadAllText(Path.Combine(target, "SKILL.md")));
        Assert.Equal("more", File.ReadAllText(Path.Combine(target, "reference.md")));
    }

    [Fact]
    public void A_different_installed_copy_is_only_replaced_with_force()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("skills/opticli");
        SkillInstaller.Install(Bundle("1.0.0"), target, force: false);

        var error = Assert.Throws<ConflictException>(() => SkillInstaller.Install(Bundle("1.2.0"), target, force: false));
        var forced = SkillInstaller.Install(Bundle("1.2.0"), target, force: true);

        Assert.Contains("1.0.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("--force", error.Hint, StringComparison.Ordinal);
        Assert.Equal(("updated", "1.0.0", "1.2.0"), (forced.Status, forced.PreviousVersion, forced.Version));
    }

    [Theory]
    [InlineData("1.0.0", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.3.0", "1.2.0", false)]
    public void Inspect_flags_copies_written_for_an_older_opticli(string installed, string running, bool outdated)
    {
        using var temp = new TempDirectory();
        SkillInstaller.Install(Bundle(installed), temp.Path, force: false);

        var skill = SkillInstaller.Inspect(SkillScope.User, temp.Path, running);

        Assert.Equal((installed, outdated), (skill!.Version, skill.Outdated));
    }

    [Fact]
    public void Inspect_counts_an_unversioned_copy_as_outdated_and_a_missing_one_as_absent()
    {
        using var temp = new TempDirectory();
        Assert.Null(SkillInstaller.Inspect(SkillScope.User, temp.Path, "1.0.0"));

        temp.Write("SKILL.md", "---\nname: opticli\n---\n");

        Assert.True(SkillInstaller.Inspect(SkillScope.User, temp.Path, "1.0.0")!.Outdated);
    }

    [Fact]
    public void Repository_root_is_the_nearest_directory_with_a_git_entry()
    {
        using var temp = new TempDirectory();
        temp.Write("repo/.git", "gitdir: elsewhere");
        Directory.CreateDirectory(temp.Combine("repo/src/Site"));

        Assert.Equal(temp.Combine("repo"), SkillLocations.FindRepositoryRoot(temp.Combine("repo/src/Site")));
        Assert.Equal(Path.Combine(temp.Combine("repo"), ".claude", "skills", "opticli"), SkillLocations.Repository(temp.Combine("repo")));
    }

    [Fact]
    public void The_bundled_skill_is_written_for_this_version_of_opticli()
    {
        // opticli embeds skill/SKILL.md; bumping <Version> without reviewing the skill fails here.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "opticli.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        var version = typeof(SkillBundle).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var skill = File.ReadAllText(Path.Combine(root.FullName, "skill", "SKILL.md"));

        Assert.Equal(version, SkillBundle.ReadVersion(skill));
        Assert.Contains($"written for opticli {version}", skill, StringComparison.Ordinal);
    }

    private static SkillBundle Bundle(string version) => new(new Dictionary<string, string>
    {
        ["SKILL.md"] = Skill.Replace("1.2.0", version),
        ["reference.md"] = "more",
    });
}
