using OptiCli.Core.Skills;

namespace OptiCli.Cli;

/// <summary>The skill files embedded in this assembly from the repository's <c>skill/</c> directory.</summary>
internal static class BundledSkill
{
    private const string ResourcePrefix = "skill/";

    public static SkillBundle Load()
    {
        var assembly = typeof(BundledSkill).Assembly;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            files[resource[ResourcePrefix.Length..]] = reader.ReadToEnd();
        }
        return new SkillBundle(files);
    }
}
