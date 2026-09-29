namespace OptiCli.Core.Skills;

public enum SkillScope
{
    /// <summary><c>~/.claude/skills/opticli/</c>: every repository on the machine.</summary>
    User,

    /// <summary><c>&lt;repository&gt;/.claude/skills/opticli/</c>: one repository, shareable through git.</summary>
    Repository,
}

/// <summary>Where Claude Code looks for skills, and so where <c>skill install</c> puts opticli's.</summary>
public static class SkillLocations
{
    public static string User(OptiCliEnvironment environment) => In(environment.HomeDirectory);

    /// <summary>The skill directory of the repository containing <paramref name="directory"/>.</summary>
    public static string Repository(string repositoryRoot) => In(repositoryRoot);

    /// <summary>
    /// The nearest directory at or above <paramref name="directory"/> with a <c>.git</c> entry (a directory,
    /// or a file in worktrees and submodules); null outside a git repository.
    /// </summary>
    public static string? FindRepositoryRoot(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return current.FullName;
            }
        }
        return null;
    }

    private static string In(string root) => Path.Combine(root, ".claude", "skills", SkillBundle.Name);
}
