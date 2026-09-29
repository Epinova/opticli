using System.Text.RegularExpressions;

namespace OptiCli.Core.Skills;

/// <summary>
/// The agent skill files opticli carries (<c>SKILL.md</c> plus files it loads on demand), keyed by file name.
/// </summary>
public sealed partial class SkillBundle
{
    public const string Name = "opticli";

    public const string MainFile = "SKILL.md";

    /// <summary>Front-matter field naming the opticli version the skill was written for.</summary>
    public const string VersionField = "opticli-version";

    public SkillBundle(IReadOnlyDictionary<string, string> files)
    {
        if (!files.TryGetValue(MainFile, out var main))
        {
            throw new ArgumentException($"A skill needs a {MainFile}.", nameof(files));
        }
        Files = files;
        Version = ReadVersion(main);
    }

    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>The <see cref="VersionField"/> of <see cref="MainFile"/>; null when it has none.</summary>
    public string? Version { get; }

    /// <summary>The <see cref="VersionField"/> value in a <c>SKILL.md</c>'s YAML front matter.</summary>
    public static string? ReadVersion(string skillMarkdown)
    {
        var frontMatter = FrontMatterPattern().Match(skillMarkdown.TrimStart('﻿'));
        if (!frontMatter.Success)
        {
            return null;
        }
        var field = VersionPattern().Match(frontMatter.Groups["yaml"].Value);
        return field.Success ? field.Groups["value"].Value.Trim().Trim('"', '\'') : null;
    }

    [GeneratedRegex(@"\A---[ \t]*\r?\n(?<yaml>.*?)\r?\n---[ \t]*(\r?\n|\z)", RegexOptions.Singleline)]
    private static partial Regex FrontMatterPattern();

    [GeneratedRegex(@"^opticli-version[ \t]*:[ \t]*(?<value>[^\r\n#]+)", RegexOptions.Multiline)]
    private static partial Regex VersionPattern();
}
