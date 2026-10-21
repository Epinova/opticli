using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OptiCli.Core.Discovery;
using OptiCli.Protocol;

namespace OptiCli.Core.Serve;

/// <summary>What <c>opticli ide on</c> put in the project's <c>.csproj.user</c>.</summary>
/// <param name="AgentDll">opticli's copy of the agent, named as the build's startup hook.</param>
/// <param name="IdeFile">Where the site announces its agent (<see cref="StateStore.IdePath"/>).</param>
/// <param name="Configuration">The build configuration that gets the hook (Debug).</param>
/// <param name="RemoteDatabase">The approved remote development database (<see cref="AgentProtocol.FormatRemote"/>); null for a local one.</param>
/// <param name="ConnectionName">The connection string the agent checks, when it isn't <see cref="AgentProtocol.DefaultConnectionName"/>.</param>
public sealed record IdeBlock(string AgentDll, string IdeFile, string Configuration, string? RemoteDatabase, string? ConnectionName);

/// <summary>Whether git would commit the project's <c>.csproj.user</c>.</summary>
public enum GitTracking
{
    /// <summary>Not in a git repository, or git isn't there.</summary>
    NoRepository,

    Ignored,

    NotIgnored,

    /// <summary>Committed: opticli doesn't put a machine path in it.</summary>
    Tracked,
}

/// <summary>
/// IDE mode's files: the labelled <c>ItemGroup</c> in <c>&lt;Project&gt;.csproj.user</c> (which MSBuild imports on its own)
/// that names the agent as the build's startup hook, opticli's copies of the agent, and the line in
/// <c>.git/info/exclude</c> that keeps the file out of git.
/// </summary>
public static partial class IdeProject
{
    public const string Label = "opticli ide";

    public const string DefaultConfiguration = "Debug";

    /// <summary>Copies of the agent this old (other than the current one) are deleted by <c>ide on</c>.</summary>
    public static readonly TimeSpan KeepCopies = TimeSpan.FromDays(30);

    private const string Comment = " opticli ide: the site agent for runs from the IDE (Rider, Visual Studio, dotnet run). Remove it with `opticli ide off`. ";
    private const string ExcludeComment = "# opticli ide: keeps the next file out of git (`opticli ide off` removes both lines)";
    private const string OptionElement = "RuntimeHostConfigurationOption";

    /// <summary><c>&lt;Project&gt;.csproj.user</c>, next to the project file.</summary>
    public static string UserFile(ProjectInfo project) => project.ProjectFile + ".user";

    /// <summary>Whether a project in <paramref name="projectDirectory"/> has IDE mode on (any <c>*.csproj.user</c> there with the block).</summary>
    public static bool IsOn(string projectDirectory)
    {
        try
        {
            return Directory.Exists(projectDirectory)
                && Directory.EnumerateFiles(projectDirectory, "*.csproj.user").Any(file => Read(file) is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Errors.UsageException)
        {
            return false;
        }
    }

    /// <returns>Null when the file or opticli's block isn't there.</returns>
    public static IdeBlock? Read(string userFile)
    {
        if (!File.Exists(userFile) || Find(Load(userFile)) is not { } group)
        {
            return null;
        }
        string? Value(string name) => group.Elements(group.Name.Namespace + OptionElement)
            .FirstOrDefault(e => (string?)e.Attribute("Include") == name)?.Attribute("Value")?.Value is { } value ? Unescape(value) : null;
        var hook = Value(AgentProtocol.StartupHooksProperty);
        var ideFile = Value(AgentProtocol.IdeFileProperty);
        if (hook is null || ideFile is null)
        {
            return null;
        }
        var configuration = ConfigurationPattern().Match((string?)group.Attribute("Condition") ?? "") is { Success: true } match
            ? Unescape(match.Groups[1].Value)
            : DefaultConfiguration;
        return new IdeBlock(hook, ideFile, configuration, Value(AgentProtocol.IdeRemoteDatabaseProperty), Value(AgentProtocol.IdeConnectionNameProperty));
    }

    /// <summary>Writes opticli's block, replacing an earlier one; everything else in the file stays.</summary>
    /// <returns>True when the file didn't exist before.</returns>
    public static bool Write(string userFile, IdeBlock block)
    {
        var created = !File.Exists(userFile);
        var document = created ? new XDocument(new XElement("Project")) : Load(userFile);
        RemoveBlock(document);
        var ns = document.Root!.Name.Namespace;
        var group = new XElement(ns + "ItemGroup",
            new XAttribute("Label", Label),
            new XAttribute("Condition", $"'$(Configuration)' == '{Escape(block.Configuration)}' And '$(_IsPublishing)' != 'true' And Exists('{Escape(block.AgentDll)}')"),
            Option(ns, AgentProtocol.StartupHooksProperty, block.AgentDll),
            Option(ns, AgentProtocol.IdeFileProperty, block.IdeFile));
        if (block.RemoteDatabase is not null)
        {
            group.Add(Option(ns, AgentProtocol.IdeRemoteDatabaseProperty, block.RemoteDatabase));
        }
        if (block.ConnectionName is not null)
        {
            group.Add(Option(ns, AgentProtocol.IdeConnectionNameProperty, block.ConnectionName));
        }
        document.Root.Add(new XComment(Comment), group);
        Save(document, userFile);
        return created;
    }

    /// <summary>Removes opticli's block; deletes the file when nothing else is left in it.</summary>
    /// <returns>Whether there was a block, and whether the file was deleted.</returns>
    public static (bool Removed, bool Deleted) Remove(string userFile)
    {
        if (!File.Exists(userFile))
        {
            return (false, false);
        }
        var document = Load(userFile);
        if (!RemoveBlock(document))
        {
            return (false, false);
        }
        if (!document.Root!.Nodes().Any(n => n is XElement or XComment))
        {
            File.Delete(userFile);
            return (true, true);
        }
        Save(document, userFile);
        return (true, false);
    }

    private static XElement Option(XNamespace ns, string name, string value) =>
        new(ns + OptionElement, new XAttribute("Include", name), new XAttribute("Value", Escape(value)));

    private static XElement? Find(XDocument document) =>
        document.Root?.Elements(document.Root.Name.Namespace + "ItemGroup").FirstOrDefault(e => (string?)e.Attribute("Label") == Label);

    private static bool RemoveBlock(XDocument document)
    {
        var removed = false;
        while (Find(document) is { } group)
        {
            if (group.PreviousNode is XComment comment && comment.Value.Contains("opticli ide", StringComparison.Ordinal))
            {
                comment.Remove();
            }
            group.Remove();
            removed = true;
        }
        return removed;
    }

    private static XDocument Load(string userFile)
    {
        try
        {
            return XDocument.Load(userFile);
        }
        catch (XmlException ex)
        {
            throw new Errors.UsageException($"{userFile} isn't valid XML, so opticli won't change it: {ex.Message}", "Fix or delete the file (MSBuild can't read it either), then run `opticli ide on` again.");
        }
    }

    private static void Save(XDocument document, string userFile)
    {
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = document.Declaration is null, Encoding = new UTF8Encoding(false) };
        var temp = $"{userFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var writer = XmlWriter.Create(temp, settings))
            {
                document.Save(writer);
            }
            File.Move(temp, userFile, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>MSBuild's escaping for the characters it treats specially in item values and conditions.</summary>
    internal static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '%' => "%25",
                '$' => "%24",
                '@' => "%40",
                '\'' => "%27",
                ';' => "%3B",
                '?' => "%3F",
                '*' => "%2A",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }

    internal static string Unescape(string value) =>
        EscapePattern().Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    [GeneratedRegex("%([0-9A-Fa-f]{2})")]
    private static partial Regex EscapePattern();

    [GeneratedRegex(@"([*?\[\]\\])")]
    private static partial Regex GlobPattern();

    [GeneratedRegex(@"'\$\(Configuration\)'\s*==\s*'([^']*)'")]
    private static partial Regex ConfigurationPattern();

    /// <summary>The hook and IDE file a build's <c>runtimeconfig.json</c> names, as <c>ide on</c> put them there; nulls when it names none.</summary>
    public static (string? AgentDll, string? IdeFile) InBuild(string outputDll)
    {
        var path = Path.ChangeExtension(outputDll, ".runtimeconfig.json");
        if (!File.Exists(path))
        {
            return (null, null);
        }
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!json.RootElement.TryGetProperty("runtimeOptions", out var options) || !options.TryGetProperty("configProperties", out var properties))
            {
                return (null, null);
            }
            string? Text(string name) => properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var agent = Text(AgentProtocol.StartupHooksProperty)?
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(hook => string.Equals(Path.GetFileName(hook), AgentLocator.FileName, StringComparison.OrdinalIgnoreCase));
            return (agent, Text(AgentProtocol.IdeFileProperty));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return (null, null);
        }
    }

    /// <summary><c>&lt;state dir&gt;/agent</c>: opticli's copies of the agent that builds name as their startup hook.</summary>
    public static string CopiesDirectory(string stateDirectory) => Path.Combine(stateDirectory, "agent");

    /// <summary>
    /// A copy of <paramref name="agentDll"/> that stays where it is when opticli is updated:
    /// <c>agent/&lt;version&gt;-&lt;hash&gt;/cms&lt;major&gt;/</c>. Another version or build gets another folder, so a DLL a
    /// running site has loaded (which Windows locks) is never overwritten.
    /// </summary>
    /// <returns>The copy's path.</returns>
    public static string CopyAgent(string stateDirectory, string agentDll, int cmsMajor)
    {
        var bytes = File.ReadAllBytes(agentDll);
        var hash = Convert.ToHexString(SHA256.HashData(bytes), 0, 4).ToLowerInvariant();
        var folder = Path.Combine(CopiesDirectory(stateDirectory), $"{AgentVersion(agentDll) ?? "0.0.0"}-{hash}", AgentLocator.Folder(cmsMajor));
        var target = Path.Combine(folder, AgentLocator.FileName);
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
        {
            return target;
        }
        Directory.CreateDirectory(folder);
        var symbols = Path.ChangeExtension(agentDll, ".pdb");
        if (File.Exists(symbols))
        {
            File.Copy(symbols, Path.ChangeExtension(target, ".pdb"), overwrite: true);
        }
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>The agent's version without the source-revision suffix; null when the file has none.</summary>
    public static string? AgentVersion(string agentDll)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(agentDll).ProductVersion?.Split('+')[0] is { Length: > 0 } version ? version : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes copies older than <see cref="KeepCopies"/>, except the folder of <paramref name="current"/>; a copy a running
    /// site holds (Windows) is skipped. A project still naming a deleted copy builds without the hook (its condition)
    /// until <c>ide on</c> runs there again.
    /// </summary>
    /// <returns>The folders deleted.</returns>
    public static IReadOnlyList<string> PruneCopies(string stateDirectory, string current, DateTime now)
    {
        var root = CopiesDirectory(stateDirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }
        var keep = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, ".."));
        var deleted = new List<string>();
        foreach (var folder in Directory.GetDirectories(root))
        {
            if (string.Equals(Path.GetFullPath(folder), keep, StringComparison.Ordinal) || now - Directory.GetLastWriteTimeUtc(folder) < KeepCopies)
            {
                continue;
            }
            try
            {
                Directory.Delete(folder, recursive: true);
                deleted.Add(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return deleted;
    }

    /// <summary>Whether git would commit <paramref name="file"/> (which needn't exist yet).</summary>
    public static GitTracking Tracking(string file)
    {
        var directory = Path.GetDirectoryName(file)!;
        var name = Path.GetFileName(file);
        if (Git.Output(directory, "rev-parse", "--show-toplevel") is null)
        {
            return GitTracking.NoRepository;
        }
        if (Git.Run(directory, "ls-files", "--error-unmatch", "--", name) is (0, _))
        {
            return GitTracking.Tracked;
        }
        return Git.Run(directory, "check-ignore", "-q", "--", name) is (0, _) ? GitTracking.Ignored : GitTracking.NotIgnored;
    }

    /// <summary>Adds <paramref name="file"/> to the repository's <c>info/exclude</c>, which is never committed.</summary>
    /// <returns>The exclude file; null when git couldn't say where it is.</returns>
    public static string? Exclude(string file)
    {
        if (ExcludeFile(file) is not { } exclude || Pattern(file) is not { } pattern)
        {
            return null;
        }
        var lines = File.Exists(exclude) ? File.ReadAllLines(exclude).ToList() : [];
        if (!lines.Contains(pattern))
        {
            lines.Add(ExcludeComment);
            lines.Add(pattern);
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            File.WriteAllLines(exclude, lines);
        }
        return exclude;
    }

    /// <summary>Removes what <see cref="Exclude"/> added (the comment and the pattern), and nothing else.</summary>
    /// <returns>The exclude file when a line was removed; otherwise null.</returns>
    public static string? Unexclude(string file)
    {
        if (ExcludeFile(file) is not { } exclude || !File.Exists(exclude) || Pattern(file) is not { } pattern)
        {
            return null;
        }
        var lines = File.ReadAllLines(exclude).ToList();
        var at = lines.FindIndex(line => line == pattern);
        if (at < 1 || lines[at - 1] != ExcludeComment)
        {
            return null;
        }
        lines.RemoveRange(at - 1, 2);
        File.WriteAllLines(exclude, lines);
        return exclude;
    }

    private static string? ExcludeFile(string file)
    {
        var directory = Path.GetDirectoryName(file)!;
        return Git.Output(directory, "rev-parse", "--git-path", "info/exclude")?.Trim() is { Length: > 0 } path
            ? Path.GetFullPath(path, directory)
            : null;
    }

    /// <summary><c>/path/from/repository/root</c>, as an exclude pattern that matches that one file.</summary>
    /// <remarks>
    /// The folder's path in the repository comes from git (<c>--show-prefix</c>), not from comparing paths: git reports the
    /// work tree's real path, which differs from ours below a symbolic link (macOS's <c>/var</c> is <c>/private/var</c>).
    /// </remarks>
    private static string? Pattern(string file)
    {
        if (Git.Output(Path.GetDirectoryName(file)!, "rev-parse", "--show-prefix") is not { } prefix)
        {
            return null;
        }
        var relative = prefix.Trim() + Path.GetFileName(file);
        // Glob characters in a folder name would otherwise make the pattern match other files, or none.
        return "/" + GlobPattern().Replace(relative, @"\$1");
    }
}
