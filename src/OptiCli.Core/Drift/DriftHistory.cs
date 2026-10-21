using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OptiCli.Core.Cms;
using OptiCli.Protocol;

namespace OptiCli.Core.Drift;

/// <summary>
/// For drift's content types and properties that only the database has: whether this checkout's git history removed
/// their class (or the property) from code, and in which commit (<see cref="DriftItem.RemovedFromCode"/>). The CMS keeps
/// a type or property whose class is gone while content uses it, so such an item needn't mean that the environment runs
/// newer code. Only looked up in a git work tree (a <c>.git</c> in the project's folder or above) with <c>git</c> on the
/// path, for at most <see cref="MaxItems"/> items within <see cref="Budget"/>; anything else (no repository, no git, a
/// timeout, an error) leaves the report as it is.
/// </summary>
/// <remarks>
/// A type is looked up by its GUID (the <c>[ContentType(GUID = ...)]</c> string, from the database), then by
/// <c>class Name</c> (not in a comment), in <c>*.cs</c> files: not in <c>HEAD</c>, but <c>git log -S</c>/<c>-G</c> finds the commit that
/// removed it; by name only when the removed class had no GUID or the type's. A property (<c>Type.Property</c>) is
/// looked up as a declaration (<c>Property {</c>, <c>Property =&gt;</c>, not in a comment) in its type's class files,
/// then in those of each base class they name (as far as <c>HEAD</c> has them), and only when none of them declares
/// it in <c>HEAD</c>. False negatives are preferred to naming an unrelated commit.
/// </remarks>
public static class DriftHistory
{
    /// <summary>The difference the agent reports for a content type or property only the database has (the agent's <c>ContentModelComparison.OnlyInDatabase</c> is the same constant).</summary>
    public const string OnlyInDatabase = DriftItem.OnlyInDatabase;

    /// <summary>How long every git call of one lookup may take together.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>Items looked up at most; the rest go without.</summary>
    public const int MaxItems = 50;

    private const string CsFiles = "*.cs";

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="report"/> has anything to look up: content types or properties only the database has.</summary>
    public static bool Applies(DriftReport report) =>
        report.ContentTypes.Concat(report.Properties).Any(Candidate);

    /// <summary>The report with <see cref="DriftItem.RemovedFromCode"/> on each item the history accounts for; the fingerprint, direction and counts stay.</summary>
    /// <param name="directory">The site project; its git work tree (from the top) is searched.</param>
    /// <param name="types">The database's content types, for their GUIDs and class names.</param>
    /// <param name="budget">Default: <see cref="Budget"/>.</param>
    public static DriftReport Annotate(DriftReport report, string? directory, IReadOnlyList<ContentTypeInfo> types, TimeSpan? budget = null)
    {
        if (directory is null || !Directory.Exists(directory) || !Applies(report))
        {
            return report;
        }
        try
        {
            if (!InRepository(directory) || Git.Open(directory, budget ?? Budget) is not { } git)
            {
                return report;
            }
            var found = new Lookup(git, types).Find(
                report.ContentTypes.Where(Candidate).Select(i => i.Name).Take(MaxItems).ToList(),
                report.Properties.Where(Candidate).Select(i => i.Name).Take(Math.Max(0, MaxItems - report.ContentTypes.Count(Candidate))).ToList());
            if (found.Count == 0)
            {
                return report;
            }
            DriftItem With(DriftItem item) => Candidate(item) && found.TryGetValue(item.Name, out var removal) ? item with { RemovedFromCode = removal } : item;
            return report with
            {
                ContentTypes = report.ContentTypes.Select(With).ToList(),
                Properties = report.Properties.Select(With).ToList(),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // History is extra: drift is reported without it, whatever went wrong (as the agent's DriftCheck does).
            return report;
        }
    }

    /// <summary>Whether every blocking item the database has and this build lacks is one the history accounts for.</summary>
    public static bool AllRemovedFromCode(DriftReport report)
    {
        var database = report.Sections().Where(s => !s.Item.Informational && s.Item.Ahead == DriftAhead.Database).ToList();
        return database.Count > 0 && database.All(s => s.Item.RemovedFromCode is not null);
    }

    /// <summary>Whether <paramref name="directory"/> is in a git work tree (<see cref="Discovery.Git.InRepository"/>).</summary>
    internal static bool InRepository(string directory) => Discovery.Git.InRepository(directory);

    private static bool Candidate(DriftItem item) =>
        !item.Informational && item.Ahead == DriftAhead.Database && item.Difference == OnlyInDatabase;

    private sealed class Lookup(Git git, IReadOnlyList<ContentTypeInfo> types)
    {
        /// <summary>Base classes followed at most for a property.</summary>
        private const int MaxBaseClasses = 5;

        private readonly Dictionary<string, ContentTypeInfo> _types = types.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        private readonly Dictionary<string, HashSet<string>> _baseFiles = new(StringComparer.Ordinal);

        /// <returns>By item name, the commits that removed them.</returns>
        public Dictionary<string, CodeRemoval> Find(IReadOnlyList<string> typeNames, IReadOnlyList<string> propertyPaths)
        {
            var found = new Dictionary<string, CodeRemoval>(StringComparer.Ordinal);
            var properties = propertyPaths
                .Select(p => (Path: p, Type: p[..Math.Max(0, p.IndexOf('.'))], Name: p[(p.IndexOf('.') + 1)..]))
                .Where(p => p.Type.Length > 0 && Identifier.IsMatch(p.Name))
                .ToList();
            var owners = typeNames.Concat(properties.Select(p => p.Type)).Distinct(StringComparer.Ordinal).ToList();

            // What HEAD still has, in one call each: files with each GUID, each class and each property declaration.
            var guidFiles = git.FilesInHead(owners.Select(GuidOf).OfType<string>().Distinct(), fixedIgnoreCase: true, key: m => m.ToLowerInvariant());
            var classFiles = git.FilesInHead(owners.Select(ClassOf).OfType<string>().Distinct().Select(ClassPattern), fixedIgnoreCase: false, key: ClassFromMatch);
            var propertyFiles = git.FilesInHead(properties.Select(p => p.Name).Distinct().Select(PropertyPattern), fixedIgnoreCase: false, key: PropertyFromMatch);
            // Any mention of the name, for the declarations PropertyPattern misses (a "/" before the name, the name on a
            // line of its own): a property HEAD still mentions in its classes isn't named as removed.
            var mentionFiles = git.FilesInHead(properties.Select(p => p.Name).Distinct().Select(MentionPattern), fixedIgnoreCase: false, key: IdentifierFromMatch);

            foreach (var name in typeNames)
            {
                if (git.OutOfTime)
                {
                    return found;
                }
                // Still in HEAD: it wasn't removed here (the build may just be older than the checkout).
                var guid = GuidOf(name);
                var className = ClassOf(name);
                if ((guid is not null && guidFiles.ContainsKey(guid)) || (className is not null && classFiles.ContainsKey(className)))
                {
                    continue;
                }
                var removal = guid is null ? null : git.LastChange(["-S", guid, "-i"], [CsFiles])?.Removal;
                if (removal is null && className is not null
                    && git.LastChange(["-G", ClassPattern(className)], [CsFiles], withFiles: true) is { } change
                    && RemovedClassIsThisType(change, className, guid))
                {
                    removal = change.Removal;
                }
                if (removal is not null)
                {
                    found[name] = removal;
                }
            }

            var cache = new Dictionary<string, CodeRemoval?>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                if (git.OutOfTime)
                {
                    return found;
                }
                // The type's class files, then its base class's (as the type's class names it), and so on: the property
                // is looked up only there, so a same-named property of an unrelated class doesn't count.
                var chain = Chain(property.Type, guidFiles, classFiles);
                var inHead = new HashSet<string>(propertyFiles.GetValueOrDefault(property.Name) ?? [], StringComparer.Ordinal);
                inHead.UnionWith(mentionFiles.GetValueOrDefault(property.Name) ?? []);
                if (chain.Count == 0 || chain.Any(files => files.Overlaps(inHead)))
                {
                    // Classes not found, or one of them still declares or mentions it (it moved to a base class, say): not removed here.
                    continue;
                }
                CodeRemoval? removal = null;
                foreach (var files in chain)
                {
                    var key = $"{property.Name}\u001f{string.Join('\u001f', files)}";
                    if (!cache.TryGetValue(key, out removal))
                    {
                        cache[key] = removal = git.LastChange(["-G", PropertyPattern(property.Name)], files, literal: true)?.Removal;
                    }
                    if (removal is not null)
                    {
                        break;
                    }
                }
                if (removal is not null)
                {
                    found[property.Path] = removal;
                }
            }
            return found;
        }

        /// <summary>
        /// Whether the class <paramref name="change"/> removed was this type's: it had no GUID, or the type's. A removed
        /// class of the same name with another GUID (or one that can't be read) was another type.
        /// </summary>
        private bool RemovedClassIsThisType(Git.Change change, string className, string? guid)
        {
            var declared = false;
            foreach (var file in change.Files)
            {
                if (git.Show($"{change.Hash}^", file) is not { } before || DeclaredGuid(before, className) is not { } attribute)
                {
                    continue;
                }
                declared = true;
                if (attribute.Length > 0 && !(guid is not null && Guid.TryParse(attribute, out var parsed) && parsed == Guid.Parse(guid)))
                {
                    return false;
                }
            }
            return declared;
        }

        /// <summary>
        /// The class files of <paramref name="type"/> in <c>HEAD</c>, then those of each base class it names that is in
        /// <c>HEAD</c> too (up to <see cref="MaxBaseClasses"/>); empty when the type's own aren't found.
        /// </summary>
        private List<SortedSet<string>> Chain(string type, Dictionary<string, HashSet<string>> guidFiles, Dictionary<string, HashSet<string>> classFiles)
        {
            var chain = new List<SortedSet<string>>();
            var files = new SortedSet<string>(StringComparer.Ordinal);
            if (GuidOf(type) is { } guid && guidFiles.TryGetValue(guid, out var withGuid))
            {
                files.UnionWith(withGuid);
            }
            var className = ClassOf(type);
            if (className is not null && classFiles.TryGetValue(className, out var withClass))
            {
                files.UnionWith(withClass);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (files.Count > 0 && className is not null && seen.Add(className) && chain.Count <= MaxBaseClasses)
            {
                chain.Add(files);
                if (BaseOf(files, className) is not { } baseClass)
                {
                    break;
                }
                files = new SortedSet<string>(BaseFiles(baseClass), StringComparer.Ordinal);
                className = baseClass;
            }
            return chain;
        }

        /// <summary>The base class <paramref name="className"/> names in <c>HEAD</c>'s <paramref name="files"/>, without its namespace and type arguments.</summary>
        private string? BaseOf(IEnumerable<string> files, string className)
        {
            foreach (var file in files)
            {
                // class Name<T>(primary constructor) : Base<T>, IInterface
                if (git.Show("HEAD", file) is { } source
                    && Regex.Match(source, $@"{Declaration(className)}(\s*<[^>{{]*>)?(\s*\([^)]*\))?\s*:\s*([A-Za-z_][A-Za-z0-9_.]*)") is { Success: true } match)
                {
                    var name = match.Groups[3].Value;
                    name = name[(name.LastIndexOf('.') + 1)..];
                    return Identifier.IsMatch(name) ? name : null;
                }
            }
            return null;
        }

        /// <summary>The files in <c>HEAD</c> that declare the class; none for a class of a package (<c>PageData</c>).</summary>
        private HashSet<string> BaseFiles(string className)
        {
            if (!_baseFiles.TryGetValue(className, out var files))
            {
                _baseFiles[className] = files = git.FilesInHead([ClassPattern(className)], fixedIgnoreCase: false, key: ClassFromMatch).GetValueOrDefault(className) ?? [];
            }
            return files;
        }

        private string? GuidOf(string type) =>
            _types.TryGetValue(type, out var info) && info.Guid != Guid.Empty ? info.Guid.ToString("D") : null;

        private string? ClassOf(string type)
        {
            var name = _types.TryGetValue(type, out var info) && info.ClassName is { Length: > 0 } className ? className : type;
            return Identifier.IsMatch(name) ? name : null;
        }

        // POSIX extended regular expressions, as git grep -E and git log -G take them on every platform (no \b). As in
        // PropertyPattern, no "/" before it on the line and not a comment line, so "// see class Name" doesn't count.
        private static string ClassPattern(string name) => $"^[[:space:]]*([^/*[:space:]][^/]*[^[:alnum:]_])?class[[:space:]]+{name}([^[:alnum:]_]|$)";

        // ClassPattern for .NET, on a file's source; the group "class" is the keyword.
        private static string Declaration(string className) => $@"(?m)^[ \t]*(?:[^/*\s][^/\n]*(?<![A-Za-z0-9_]))?(?<class>class)\s+{className}(?![A-Za-z0-9_])";

        // A declaration: something before the name (its type), no "/" before it on the line, and not a line that starts a
        // comment ("//", "///", "/*") or goes on with one ("*").
        private static string PropertyPattern(string name) => $"^[[:space:]]*[^/*[:space:]][^/]*[^[:alnum:]_./\"]({name})[[:space:]]*(\\{{|=>|$)";

        /// <summary>
        /// The GUID the attributes of <paramref name="className"/>'s declaration in <paramref name="source"/> give: null when
        /// it isn't declared there, empty when it has no GUID, otherwise the string after <c>GUID =</c> (<c>?</c> when it isn't a literal).
        /// </summary>
        private static string? DeclaredGuid(string source, string className)
        {
            var declaration = Regex.Match(source, Declaration(className));
            if (!declaration.Success)
            {
                return null;
            }
            // Its attributes: back to the end of the previous member, type or directive.
            var before = source[..declaration.Groups["class"].Index];
            before = before[(Math.Max(before.LastIndexOf('}'), before.LastIndexOf(';')) + 1)..];
            var guid = Regex.Match(before, @"\bGUID\s*=\s*(""(?<value>[^""]*)"")?", RegexOptions.IgnoreCase);
            return !guid.Success ? "" : guid.Groups["value"].Success ? guid.Groups["value"].Value : "?";
        }

        // The match starts at the line's start: the class is the last one it names.
        private static string ClassFromMatch(string match) => Regex.Match(match, @"class\s+([A-Za-z0-9_]+)", RegexOptions.RightToLeft).Groups[1].Value;

        // The name on its own, anywhere but after "." or a quote (comment lines included: a false negative is fine).
        private static string MentionPattern(string name) => $"(^|[^[:alnum:]_.\"]){name}([^[:alnum:]_]|$)";

        private static string IdentifierFromMatch(string match) => Regex.Match(match, "[A-Za-z_][A-Za-z0-9_]*").Value;

        private static string PropertyFromMatch(string match) => Regex.Match(match, @"([A-Za-z_][A-Za-z0-9_]*)\s*(\{|=>)?\s*$").Groups[1].Value;
    }

    /// <summary>
    /// <c>git</c> as a child process, at the top of the work tree, within one time budget for every call; a call that
    /// fails, can't start or runs out of time answers null.
    /// </summary>
    internal sealed class Git
    {
        private readonly string _top;
        private readonly Stopwatch _clock;
        private readonly TimeSpan _budget;

        private Git(string top, Stopwatch clock, TimeSpan budget)
        {
            _top = top;
            _clock = clock;
            _budget = budget;
        }

        public bool OutOfTime => Remaining <= TimeSpan.Zero;

        private TimeSpan Remaining => _budget - _clock.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

        /// <summary>Null when <paramref name="directory"/> isn't in a git work tree with a commit, or git doesn't run.</summary>
        public static Git? Open(string directory, TimeSpan budget)
        {
            var clock = Stopwatch.StartNew();
            var probe = new Git(directory, clock, budget);
            var top = probe.Run(["rev-parse", "--show-toplevel", "--verify", "-q", "HEAD"])?.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return top is { Length: 2 } && Directory.Exists(top[0].Trim()) ? new Git(top[0].Trim(), clock, budget) : null;
        }

        /// <summary>
        /// <c>git grep -o</c> over <c>HEAD</c>'s <c>*.cs</c> files: by <paramref name="key"/> of each match, the files it is in.
        /// Nothing for no patterns, no match or a failure.
        /// </summary>
        /// <param name="fixedIgnoreCase">Fixed strings, any case (GUIDs); otherwise extended regular expressions.</param>
        public Dictionary<string, HashSet<string>> FilesInHead(IEnumerable<string> patterns, bool fixedIgnoreCase, Func<string, string> key)
        {
            var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var arguments = new List<string> { "grep", "-I", "-o", "-z", "--no-color", fixedIgnoreCase ? "-F" : "-E" };
            if (fixedIgnoreCase)
            {
                arguments.Add("-i");
            }
            var count = arguments.Count;
            foreach (var pattern in patterns)
            {
                arguments.Add("-e");
                arguments.Add(pattern);
            }
            if (arguments.Count == count)
            {
                return found;
            }
            arguments.AddRange(["HEAD", "--", CsFiles]);
            // Lines are "HEAD:<path>\0<match>"; exit code 1 is no match.
            foreach (var line in (Run(arguments, noMatch: 1) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('\0');
                if (separator < 0)
                {
                    continue;
                }
                var path = line[..separator];
                path = path.StartsWith("HEAD:", StringComparison.Ordinal) ? path["HEAD:".Length..] : path;
                var matched = key(line[(separator + 1)..]);
                if (matched.Length > 0)
                {
                    (found.TryGetValue(matched, out var files) ? files : found[matched] = new HashSet<string>(StringComparer.Ordinal)).Add(path);
                }
            }
            return found;
        }

        /// <summary>A commit <see cref="LastChange"/> found.</summary>
        /// <param name="Hash">The full hash.</param>
        /// <param name="Files">With <c>withFiles</c>: the files of <c>paths</c> it changed.</param>
        public sealed record Change(CodeRemoval Removal, string Hash, IReadOnlyList<string> Files);

        /// <summary>The newest commit whose changes to <paramref name="paths"/> match <paramref name="pickaxe"/> (<c>-S</c>/<c>-G</c>); null for none.</summary>
        /// <param name="literal">The paths are file names, not patterns.</param>
        /// <param name="withFiles">Also list the files it changed.</param>
        public Change? LastChange(IReadOnlyList<string> pickaxe, IEnumerable<string> paths, bool literal = false, bool withFiles = false)
        {
            List<string> arguments = [.. literal ? new[] { "--literal-pathspecs" } : [], "log", "-1", "--format=%h%x1f%H%x1f%ad%x1f%s", "--date=short",
                .. withFiles ? new[] { "--name-only", "-z" } : [], .. pickaxe, "--", .. paths];
            // With -z the header ends in a NUL, and so does each file name.
            var parts = Run(arguments)?.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var fields = parts?.FirstOrDefault()?.Trim('\n', '\r').Split('\u001f');
            return fields is { Length: 4 } && fields[0].Length > 0
                ? new Change(new CodeRemoval(fields[0], fields[2], fields[3]), fields[1], parts!.Skip(1).Select(f => f.Trim('\n', '\r')).Where(f => f.Length > 0).ToList())
                : null;
        }

        /// <summary><c>git show &lt;revision&gt;:&lt;path&gt;</c>: the file as it was; null when it wasn't there (or git fails).</summary>
        public string? Show(string revision, string path) => Run(["show", $"{revision}:{path}"]);

        /// <returns>Standard output; null when git fails, can't start or runs out of time.</returns>
        /// <param name="noMatch">An exit code that means "nothing found" (empty output), as grep's 1.</param>
        private string? Run(IReadOnlyList<string> arguments, int? noMatch = null)
        {
            var left = Remaining;
            if (left <= TimeSpan.Zero)
            {
                return null;
            }
            var info = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                WorkingDirectory = _top,
                CreateNoWindow = true,
            };
            // The user's configuration mustn't change the output this reads.
            foreach (var setting in new[] { "core.quotePath=false", "color.ui=false", "log.showSignature=false", "grep.lineNumber=false", "grep.column=false", "grep.fullName=false" })
            {
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add(setting);
            }
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
            // Never ask anything, and take no lock a running git (an IDE's) would wait on.
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            info.Environment["GIT_PAGER"] = "cat";
            // A partial clone would otherwise fetch the old blobs that git log -S/-G reads from its remote.
            info.Environment["GIT_NO_LAZY_FETCH"] = "1";

            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // No git on the path.
                return null;
            }
            if (process is null)
            {
                return null;
            }
            using (process)
            {
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                // Read so a long error message can't fill the pipe and stall git; what it says isn't used.
                _ = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(left) || !output.Wait(Remaining))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        // Gone already.
                    }
                    return null;
                }
                return process.ExitCode == 0 ? output.Result : process.ExitCode == noMatch ? "" : null;
            }
        }
    }
}
