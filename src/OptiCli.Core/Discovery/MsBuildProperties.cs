using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace OptiCli.Core.Discovery;

/// <summary>
/// A small stand-in for MSBuild's property evaluation, enough for the few properties opticli reads. Like MSBuild it
/// reads the nearest <c>Directory.Build.props</c> above the project (and whatever that imports, where the import
/// says), then the project file, then the nearest <c>Directory.Build.targets</c> (which the SDK imports after the
/// project), in document order; the last definition wins. <c>$(Name)</c> is expanded from
/// properties defined so far. Conditions are evaluated when they are plain <c>'a' == 'b'</c> / <c>!=</c>
/// comparisons, <c>Exists('…')</c>, or <c>and</c>/<c>or</c> chains of those, with <c>Configuration</c> taken as
/// <c>Debug</c>; anything else (property functions, items, <c>Choose</c>) is left alone, and when that touches a
/// property opticli reports, a warning says so.
/// </summary>
internal sealed partial class MsBuildProperties
{
    public const string DirectoryBuildProps = "Directory.Build.props";

    public const string DirectoryBuildTargets = "Directory.Build.targets";

    /// <summary>Guards against import cycles and runaway chains.</summary>
    private const int MaxImportDepth = 16;

    /// <summary>The properties opticli reports. Only these produce warnings.</summary>
    private static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase)
    {
        "UserSecretsId", "TargetFramework", "TargetFrameworks", "AssemblyName", "OutputPath", "BaseOutputPath", "UseArtifactsOutput", "ArtifactsPath",
    };

    /// <summary>Path properties keep <c>$(Configuration)</c> as written: their reader fills in each configuration itself.</summary>
    private static readonly HashSet<string> PerConfiguration = new(StringComparer.OrdinalIgnoreCase) { "OutputPath", "BaseOutputPath", "ArtifactsPath" };

    /// <summary>What conditions see for properties nobody set: <c>dotnet run</c> builds Debug.</summary>
    private static readonly Dictionary<string, string> ConditionDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Configuration"] = "Debug",
        ["Platform"] = "AnyCPU",
    };

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // MSBuild property names are case-insensitive.
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _imported = [];
    private readonly List<string> _warnings = [];
    private readonly HashSet<string> _open = new(PathComparer);
    private readonly string _projectDirectory;

    private MsBuildProperties(string projectPath)
    {
        _projectDirectory = Path.GetDirectoryName(projectPath)!;
        _values["MSBuildProjectFullPath"] = projectPath;
        _values["MSBuildProjectDirectory"] = _projectDirectory;
        _values["MSBuildProjectFile"] = Path.GetFileName(projectPath);
        _values["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(projectPath);
        _values["MSBuildProjectExtension"] = Path.GetExtension(projectPath);
    }

    /// <summary>Files read besides the project file, in evaluation order.</summary>
    public IReadOnlyList<string> Imported => _imported;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The value as evaluated, null when unset or empty. An unexpandable reference is left as written.</summary>
    public string? this[string name] => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>
    /// Evaluates the nearest <c>Directory.Build.props</c>, then <paramref name="project"/> (the loaded project file), then
    /// the nearest <c>Directory.Build.targets</c>, whose properties win over the project's.
    /// </summary>
    /// <param name="readBuildProps">False evaluates the project file alone.</param>
    public static MsBuildProperties Evaluate(string projectPath, XElement project, bool readBuildProps = true)
    {
        var properties = new MsBuildProperties(projectPath);
        if (readBuildProps && FindAbove(properties._projectDirectory, DirectoryBuildProps) is { } props)
        {
            properties.Import(props, 0);
        }
        properties.ReadFile(projectPath, project, 0);
        if (readBuildProps && FindAbove(properties._projectDirectory, DirectoryBuildTargets) is { } targets)
        {
            properties.Import(targets, 0);
        }
        properties.CheckReported();
        return properties;
    }

    private void Import(string path, int depth)
    {
        if (depth > MaxImportDepth || !_open.Add(path))
        {
            Warn($"{Relative(path)} imports itself (directly or through other files); opticli read it once.");
            return;
        }
        try
        {
            XDocument document;
            try
            {
                document = XDocument.Load(path);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                Warn($"Could not read {Relative(path)}: {ex.Message}");
                return;
            }
            if (document.Root is { } root)
            {
                _imported.Add(path);
                ReadFile(path, root, depth);
            }
        }
        finally
        {
            _open.Remove(path);
        }
    }

    private void ReadFile(string path, XElement root, int depth)
    {
        // MSBuildThisFile* name the file being read, so they change on the way in and back out of an import.
        string[] fileScoped = ["MSBuildThisFile", "MSBuildThisFileName", "MSBuildThisFileExtension", "MSBuildThisFileFullPath", "MSBuildThisFileDirectory"];
        var outer = fileScoped.ToDictionary(n => n, n => _values.GetValueOrDefault(n));
        _values["MSBuildThisFile"] = Path.GetFileName(path);
        _values["MSBuildThisFileName"] = Path.GetFileNameWithoutExtension(path);
        _values["MSBuildThisFileExtension"] = Path.GetExtension(path);
        _values["MSBuildThisFileFullPath"] = path;
        _values["MSBuildThisFileDirectory"] = Path.GetDirectoryName(path) + Path.DirectorySeparatorChar;
        try
        {
            ReadElements(path, root.Elements(), depth);
        }
        finally
        {
            foreach (var (name, value) in outer)
            {
                if (value is null)
                {
                    _values.Remove(name);
                }
                else
                {
                    _values[name] = value;
                }
            }
        }
    }

    private void ReadElements(string path, IEnumerable<XElement> elements, int depth)
    {
        foreach (var element in elements)
        {
            switch (element.Name.LocalName)
            {
                case "PropertyGroup":
                    if (Applies(path, element) is true)
                    {
                        foreach (var property in element.Elements())
                        {
                            if (Applies(path, property) is true)
                            {
                                Set(path, property.Name.LocalName, property.Value);
                            }
                        }
                    }
                    break;
                case "ImportGroup" when Condition(path, element) is true:
                    ReadElements(path, element.Elements().Where(e => e.Name.LocalName == "Import"), depth);
                    break;
                case "Import":
                    ImportElement(path, element, depth);
                    break;
                case "Choose" when SetsReported(element.Descendants()):
                    Warn($"{Relative(path)} sets {ReportedNames(element.Descendants())} inside <Choose>, which opticli does not evaluate; ignored.");
                    break;
            }
        }
    }

    private void ImportElement(string path, XElement element, int depth)
    {
        if (element.Attribute("Sdk") is not null || (string?)element.Attribute("Project") is not { } project || string.IsNullOrWhiteSpace(project))
        {
            return;
        }
        var (expanded, resolved) = Expand(path, project, inCondition: false);
        if (!resolved)
        {
            // An SDK or Visual Studio targets path can't matter for the properties opticli reads; a props file might.
            if (!project.Trim().EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            {
                Warn($"{Relative(path)} imports '{project}', which opticli can't expand; properties set there are ignored.");
            }
            return;
        }
        if (string.IsNullOrWhiteSpace(expanded) || expanded.Contains('*', StringComparison.Ordinal))
        {
            return;
        }
        var file = FullPath(path, expanded);
        // An import condition opticli can't evaluate is nearly always an existence check.
        if (Condition(path, element) is not false && File.Exists(file))
        {
            Import(file, depth + 1);
        }
    }

    /// <returns>True when the element has no condition or it holds, false when it doesn't, null when opticli can't tell (then it warns if that hides a reported property).</returns>
    private bool? Applies(string path, XElement element)
    {
        var result = Condition(path, element);
        if (result is null)
        {
            IEnumerable<XElement> hidden = element.Name.LocalName == "PropertyGroup" ? element.Elements() : [element];
            if (SetsReported(hidden))
            {
                Warn($"{Relative(path)} sets {ReportedNames(hidden)} under the condition \"{(string?)element.Attribute("Condition")}\", which opticli can't evaluate; ignored.");
            }
        }
        return result;
    }

    /// <summary>The element's <c>Condition</c>: true when there is none, null when it can't be evaluated.</summary>
    private bool? Condition(string path, XElement element) =>
        (string?)element.Attribute("Condition") is { } condition && !string.IsNullOrWhiteSpace(condition)
            ? EvaluateCondition(path, condition)
            : true;

    private bool? EvaluateCondition(string path, string condition)
    {
        bool? any = false;
        foreach (var alternative in SplitOn(condition, "or"))
        {
            bool? all = true;
            foreach (var term in SplitOn(alternative, "and"))
            {
                all = Term(path, term.Trim()) switch
                {
                    false => false,
                    null when all is not false => null,
                    _ => all,
                };
            }
            any = (any, all) switch
            {
                (true, _) or (_, true) => true,
                (null, _) or (_, null) => null,
                _ => false,
            };
        }
        return any;
    }

    private bool? Term(string path, string term)
    {
        if (ExistsTerm().Match(term) is { Success: true } exists)
        {
            var (target, resolved) = Expand(path, exists.Groups["path"].Value, inCondition: true);
            if (!resolved)
            {
                return null;
            }
            var found = !string.IsNullOrWhiteSpace(target) && (File.Exists(FullPath(path, target)) || Directory.Exists(FullPath(path, target)));
            return exists.Groups["not"].Success ? !found : found;
        }
        if (ComparisonTerm().Match(term) is { Success: true } comparison)
        {
            var (left, leftResolved) = Expand(path, comparison.Groups["left"].Value, inCondition: true);
            var (right, rightResolved) = Expand(path, comparison.Groups["right"].Value, inCondition: true);
            if (!leftResolved || !rightResolved)
            {
                return null;
            }
            var equal = string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            return comparison.Groups["op"].Value == "==" ? equal : !equal;
        }
        return null;
    }

    private void Set(string path, string name, string value)
    {
        var (expanded, resolved) = Expand(path, value.Trim(), inCondition: false, property: name);
        _values[name] = expanded;
        _origins[name] = path;
        if (resolved)
        {
            _unresolved.Remove(name);
        }
        else
        {
            _unresolved.Add(name);
        }
    }

    /// <summary>
    /// Replaces <c>$(Name)</c> with properties defined so far, and evaluates <c>GetPathOfFileAbove</c> /
    /// <c>GetDirectoryNameOfFileAbove</c>. Anything else stays as written and makes the result unresolved.
    /// </summary>
    /// <param name="inCondition">In a condition an undefined property is empty, as in MSBuild; in a value opticli can't tell
    /// whether the SDK or the environment would have set it, so it is left as written.</param>
    /// <param name="property">The property being set, if any (path properties keep <c>$(Configuration)</c>).</param>
    private (string Value, bool Resolved) Expand(string path, string text, bool inCondition, string? property = null)
    {
        if (!text.Contains("$(", StringComparison.Ordinal))
        {
            return (text, true);
        }
        var result = new System.Text.StringBuilder();
        var resolved = true;
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf("$(", i, StringComparison.Ordinal);
            var end = start < 0 ? -1 : Closing(text, start + 1);
            if (start < 0 || end < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }
            result.Append(text, i, start - i);
            var raw = text[start..(end + 1)];
            var inner = text[(start + 2)..end].Trim();
            if (PropertyName().IsMatch(inner))
            {
                if (_values.TryGetValue(inner, out var value))
                {
                    result.Append(value);
                    resolved &= !_unresolved.Contains(inner);
                }
                else if (inCondition)
                {
                    result.Append(ConditionDefaults.GetValueOrDefault(inner, ""));
                }
                else
                {
                    result.Append(raw);
                    // Left for the reader of a per-configuration path, which fills in each configuration.
                    resolved &= property is not null && PerConfiguration.Contains(property) && string.Equals(inner, "Configuration", StringComparison.OrdinalIgnoreCase);
                }
            }
            else if (FileAbove(path, inner) is { } found)
            {
                result.Append(found);
            }
            else
            {
                result.Append(raw);
                resolved = false;
            }
            i = end + 1;
        }
        return (result.ToString(), resolved);
    }

    /// <summary><c>[MSBuild]::GetPathOfFileAbove(file, dir)</c> and <c>[MSBuild]::GetDirectoryNameOfFileAbove(dir, file)</c>: the match or empty, or null for any other function.</summary>
    private string? FileAbove(string path, string function)
    {
        if (FileAboveCall().Match(function) is not { Success: true } call)
        {
            return null;
        }
        var arguments = SplitArguments(call.Groups["args"].Value);
        var pathOfFile = call.Groups["name"].Value.Equals("GetPathOfFileAbove", StringComparison.OrdinalIgnoreCase);
        if (arguments.Count is 0 or > 2 || (!pathOfFile && arguments.Count != 2))
        {
            return null;
        }

        var expanded = new List<string>();
        foreach (var argument in arguments)
        {
            var (value, resolved) = Expand(path, argument, inCondition: false);
            if (!resolved)
            {
                return null;
            }
            expanded.Add(value);
        }
        var (fileName, startDirectory) = pathOfFile
            ? (expanded[0], expanded.Count > 1 ? expanded[1] : _values["MSBuildThisFileDirectory"])
            : (expanded[1], expanded[0]);
        var found = FindAbove(FullPath(path, startDirectory), fileName.Trim());
        return pathOfFile ? found ?? "" : found is null ? "" : Path.GetDirectoryName(found);
    }

    /// <summary>Function arguments, separated by commas, quoted or not.</summary>
    private static List<string> SplitArguments(string text) =>
        SplitTopLevel(text, ',')
            .Select(part => part.Trim())
            .Select(part => part.Length >= 2 && part[0] == '\'' && part[^1] == '\'' ? part[1..^1] : part)
            .ToList();

    /// <summary>Splits on a separator outside quotes and parentheses.</summary>
    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\'' when depth == 0:
                    quoted = !quoted;
                    break;
                case '(' when !quoted:
                    depth++;
                    break;
                case ')' when !quoted:
                    depth--;
                    break;
                case var c when c == separator && depth == 0 && !quoted:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return text[start..];
    }

    /// <summary>Splits a condition on a whitespace-delimited keyword (<c>and</c>, <c>or</c>) outside quotes and parentheses.</summary>
    private static IEnumerable<string> SplitOn(string condition, string keyword)
    {
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < condition.Length; i++)
        {
            var c = condition[i];
            if (c == '\'' && depth == 0)
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '(')
            {
                depth++;
            }
            else if (!quoted && c == ')')
            {
                depth--;
            }
            else if (!quoted && depth == 0 && char.IsWhiteSpace(c)
                && i + keyword.Length + 1 < condition.Length
                && string.Compare(condition, i + 1, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
                && char.IsWhiteSpace(condition[i + keyword.Length + 1]))
            {
                yield return condition[start..i];
                i += keyword.Length + 1;
                start = i;
            }
        }
        yield return condition[start..];
    }

    /// <summary>The index of the parenthesis closing the one at <paramref name="open"/>, or -1.</summary>
    private static int Closing(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Reports the properties opticli uses whose value still holds something it couldn't expand.</summary>
    private void CheckReported()
    {
        foreach (var name in Reported.Where(_unresolved.Contains).Order(StringComparer.OrdinalIgnoreCase))
        {
            var consequence = name.Equals("UserSecretsId", StringComparison.OrdinalIgnoreCase) ? " User secrets are not read." : "";
            Warn($"{name} is '{_values[name]}' ({Relative(_origins[name])}), which opticli can't expand fully; it is left as written.{consequence}");
        }
    }

    private static bool SetsReported(IEnumerable<XElement> elements) => elements.Any(e => Reported.Contains(e.Name.LocalName));

    private static string ReportedNames(IEnumerable<XElement> elements) =>
        string.Join(", ", elements.Select(e => e.Name.LocalName).Where(Reported.Contains).Distinct(StringComparer.OrdinalIgnoreCase));

    private void Warn(string message)
    {
        if (!_warnings.Contains(message))
        {
            _warnings.Add(message);
        }
    }

    private string Relative(string path) => Path.GetRelativePath(_projectDirectory, path).Replace('\\', '/');

    /// <summary>A path as MSBuild resolves it: relative to the file it is written in, with either slash.</summary>
    private static string FullPath(string file, string path)
    {
        var normalised = Path.DirectorySeparatorChar == '\\' ? path.Trim() : path.Trim().Replace('\\', '/');
        var directory = Path.GetDirectoryName(file)!;
        return normalised.Length == 0 ? directory : Path.GetFullPath(normalised, directory);
    }

    /// <summary>The nearest <paramref name="fileName"/> in <paramref name="directory"/> or above it.</summary>
    private static string? FindAbove(string directory, string fileName)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            var candidate = Path.Combine(current, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*$")]
    private static partial Regex PropertyName();

    [GeneratedRegex(@"^\[MSBuild\]::(?<name>GetPathOfFileAbove|GetDirectoryNameOfFileAbove)\((?<args>.*)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FileAboveCall();

    [GeneratedRegex(@"^(?<not>!\s*)?Exists\(\s*'(?<path>[^']*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExistsTerm();

    [GeneratedRegex(@"^'(?<left>[^']*)'\s*(?<op>==|!=)\s*'(?<right>[^']*)'$")]
    private static partial Regex ComparisonTerm();
}
