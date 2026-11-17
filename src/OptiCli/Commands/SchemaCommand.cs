using System.Collections;
using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Help;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OptiCli.Cli;
using OptiCli.Core.Output;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// <c>opticli schema</c>, hidden: every command with an action, its arguments and options, and the type of its
/// <c>data</c> as JSON Schema (2020-12), for generating the npm package's TypeScript. Result types are described as
/// <see cref="JsonOutput.Options"/> writes them, by reflection: camelCase names, nulls left out (so a nullable property
/// may be absent, not <c>null</c>), enums as camelCase strings, each named type once in <c>$defs</c>, with its XML doc
/// comments as <c>description</c>.
/// </summary>
internal static partial class SchemaCommand
{
    public const string SchemaVersion = "https://json-schema.org/draft/2020-12/schema";

    public static Command Create(GlobalOptions options)
    {
        var command = new Command("schema", """
            Internal: the commands, their options and result types as JSON Schema, for generating the npm package's TypeScript.
            Example: opticli schema --json
            """)
        {
            Hidden = true,
        };
        // The schema itself is a JSON document of its own: hidden commands aren't described, this one included.
        CommandRunner.SetHandler<JsonObject>(command, options, (_, _) =>
            Task.FromResult(new CommandResult<JsonObject>(Describe(Program.CreateRoot(new GlobalOptions())), Source: CommandResult.CliSource)));
        return command;
    }

    /// <summary>The schema of every visible command under <paramref name="root"/> that has an action.</summary>
    public static JsonObject Describe(RootCommand root)
    {
        var commands = Walk(root, []).Where(c => c.Path.Count > 0 && CommandRunner.Results(c.Command) is not null).ToList();
        var types = new TypeSchemas(XmlDocs.Load(), commands.SelectMany(c => CommandRunner.Results(c.Command)!.Modes.Select(m => m.Result)));
        var described = new JsonArray();
        foreach (var (path, command) in commands)
        {
            described.Add(Pruned(DescribeCommand(path, command, types)));
        }
        return new JsonObject
        {
            ["$schema"] = SchemaVersion,
            ["title"] = "opticli commands",
            ["opticli"] = ToolInfo.Version,
            ["globalOptions"] = new JsonArray([.. root.Options.Where(o => o.Recursive && !o.Hidden && o is not HelpOption && o is not VersionOption).Select(o => (JsonNode)Pruned(DescribeOption(o)))]),
            ["commands"] = described,
            ["$defs"] = types.Definitions,
        };
    }

    private static JsonObject DescribeCommand(IReadOnlyList<string> path, Command command, TypeSchemas types)
    {
        var modes = CommandRunner.Results(command)!.Modes;
        // Per mode: access reads without a change option, apply --seal isn't a write (as the runner sees it).
        var writes = WriteCommands.WritesPerMode(command, [.. modes.Select(m => m.When)]);
        var entry = new JsonObject
        {
            ["path"] = new JsonArray([.. path.Select(p => (JsonNode)p)]),
            ["description"] = command.Description,
            // A hidden argument is still the command's (projects' <project>, left out of its help): a mode may depend on it.
            ["arguments"] = new JsonArray([.. command.Arguments.Select(a => (JsonNode)Pruned(DescribeArgument(a)))]),
            // --jsonl only changes how a list is printed; a caller that parses the envelope never needs it.
            ["options"] = new JsonArray([.. command.Options.Where(o => !o.Hidden && !o.Recursive && o.Name != "--jsonl" && o is not HelpOption).Select(o => (JsonNode)Pruned(DescribeOption(o)))]),
            // As result: the Otherwise mode's; each mode has its own.
            ["writes"] = writes[^1],
            ["lists"] = command.Options.Any(o => o.Name == "--cursor"),
        };
        if (WriteCommands.LocalWrite(command) is { } note)
        {
            entry["writesNote"] = note;
        }
        // The last mode is the one without a condition (Otherwise): the result unless an earlier mode's condition holds.
        var fallback = modes[^1];
        AddResult(entry, fallback.Result, types);
        if (modes.Count > 1)
        {
            entry["modes"] = new JsonArray([.. modes.Take(modes.Count - 1).Select((m, i) =>
            {
                var mode = new JsonObject { ["when"] = m.When!.Describe() };
                AddResult(mode, m.Result, types);
                mode["writes"] = writes[i];
                return (JsonNode)mode;
            })]);
        }
        return entry;
    }

    /// <summary><c>result</c>, or <c>raw: true</c> for output printed as-is (<see cref="RawOutput"/>), which has no envelope.</summary>
    private static void AddResult(JsonObject entry, Type result, TypeSchemas types)
    {
        if (result == typeof(RawOutput))
        {
            entry["raw"] = true;
        }
        else
        {
            entry["result"] = types.Schema(result, null);
        }
    }

    /// <summary>Without the properties that have no value (a command or option without a description, say).</summary>
    private static JsonObject Pruned(JsonObject node)
    {
        foreach (var key in node.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            node.Remove(key);
        }
        return node;
    }

    private static IEnumerable<(IReadOnlyList<string> Path, Command Command)> Walk(Command command, IReadOnlyList<string> path)
    {
        yield return (path, command);
        foreach (var sub in command.Subcommands.Where(c => !c.Hidden))
        {
            foreach (var found in Walk(sub, [.. path, sub.Name]))
            {
                yield return found;
            }
        }
    }

    private static JsonObject DescribeOption(Option option)
    {
        var threeState = ThreeStateOptions.IsThreeState(option);
        var node = new JsonObject
        {
            ["name"] = option.Name,
            ["type"] = ValueSchema(option.ValueType, Allowed(option.ValueType, option.GetCompletions(CompletionContext.Empty))),
            ["repeats"] = option.Arity.MaximumNumberOfValues > 1,
            ["required"] = option.Required,
            ["description"] = option.Description,
        };
        if (threeState)
        {
            // Left out, the command takes the user config's setting; false must be passed as `--name false`.
            node["explicitFalse"] = true;
        }
        if (option.Aliases.Count > 0)
        {
            node["aliases"] = new JsonArray([.. option.Aliases.Order(StringComparer.Ordinal).Select(a => (JsonNode)a)]);
        }
        if (option.HelpName is { } helpName)
        {
            node["helpName"] = helpName;
        }
        if (!threeState && option.HasDefaultValue && option.GetDefaultValue() is { } value)
        {
            node["default"] = JsonOutput.ToNode(value);
        }
        return node;
    }

    private static JsonObject DescribeArgument(Argument argument)
    {
        var node = new JsonObject
        {
            ["name"] = argument.Name,
            ["type"] = ValueSchema(argument.ValueType, Allowed(argument.ValueType, argument.GetCompletions(CompletionContext.Empty))),
            ["minimum"] = argument.Arity.MinimumNumberOfValues,
            ["maximum"] = argument.Arity.MaximumNumberOfValues == ArgumentArity.ZeroOrMore.MaximumNumberOfValues ? null : argument.Arity.MaximumNumberOfValues,
            ["description"] = argument.Description,
            ["hidden"] = argument.Hidden ? true : null,
        };
        if (argument.HelpName is { } helpName)
        {
            node["helpName"] = helpName;
        }
        if (argument.HasDefaultValue && argument.GetDefaultValue() is { } value)
        {
            node["default"] = JsonOutput.ToNode(value);
        }
        return node;
    }

    /// <summary>The values <c>AcceptOnlyFromAmong</c> (or an enum) allows; a bool's true and false are no list worth giving.</summary>
    private static List<string>? Allowed(Type valueType, IEnumerable<CompletionItem> completions)
    {
        var element = valueType.IsArray ? valueType.GetElementType()! : Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (element == typeof(bool))
        {
            return null;
        }
        var values = completions.Select(c => c.Label).ToList();
        return values.Count > 0 ? values : null;
    }

    private static JsonObject ValueSchema(Type type, List<string>? allowed)
    {
        if (type.IsArray)
        {
            return new JsonObject { ["type"] = "array", ["items"] = ValueSchema(type.GetElementType()!, allowed) };
        }
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var schema = new JsonObject
        {
            ["type"] = underlying == typeof(bool) ? "boolean"
                : underlying == typeof(int) || underlying == typeof(long) ? "integer"
                : underlying == typeof(double) ? "number"
                : "string",
        };
        if (allowed is not null)
        {
            schema["enum"] = new JsonArray([.. allowed.Select(a => (JsonNode)a)]);
        }
        return schema;
    }

    /// <summary>
    /// JSON Schema for result types, as <see cref="JsonOutput.Options"/> writes them. Named types (records, classes,
    /// enums, polymorphic bases) are described once in <see cref="Definitions"/> and referenced with <c>$ref</c>.
    /// </summary>
    internal sealed class TypeSchemas
    {
        private readonly XmlDocs _docs;
        private readonly NullabilityInfoContext _nullability = new();
        private readonly Dictionary<Type, string> _names;
        private readonly HashSet<Type> _defined = [];

        /// <param name="roots">The declared result types; every type they reach gets its name up front, so two types that would share a name fail, whichever is met first.</param>
        public TypeSchemas(XmlDocs docs, IEnumerable<Type> roots)
        {
            _docs = docs;
            var reached = new HashSet<Type>();
            foreach (var root in roots)
            {
                Reach(root, reached);
            }
            _names = Names(reached);
        }

        public JsonObject Definitions { get; } = new();

        /// <summary>
        /// The name of each named type: its own (<c>WriteOutput</c>), or a value set's <see cref="ValueSetNameAttribute"/>.
        /// A name in <c>$defs</c> is the npm package's TypeScript type name, so it never changes once published: two types
        /// with one name are an error (rename the newer C# type), never qualified.
        /// </summary>
        /// <exception cref="InvalidOperationException">Two types have the same name.</exception>
        private static Dictionary<Type, string> Names(IEnumerable<Type> types)
        {
            var names = types.ToDictionary(t => t, SimpleName);
            if (names.GroupBy(n => n.Value).FirstOrDefault(g => g.Count() > 1) is { } clash)
            {
                throw new InvalidOperationException(
                    $"Output types {string.Join(" and ", clash.Select(c => c.Key.FullName))} would both be named {clash.Key} in the schema. A published name never changes: rename the newer C# type.");
            }
            return names;
        }

        private static string SimpleName(Type type) =>
            type.GetCustomAttribute<ValueSetNameAttribute>()?.Name
            ?? (type.IsGenericType
                ? type.Name[..type.Name.IndexOf('`')] + "Of" + string.Concat(type.GetGenericArguments().Select(SimpleName))
                : type.Name);

        /// <summary>A class of string constants that <see cref="OutputValuesAttribute"/> names: a static class.</summary>
        private static bool IsValueSet(Type type) => type is { IsClass: true, IsAbstract: true, IsSealed: true };

        /// <summary>A value set's values: its public <c>const string</c> fields, in order.</summary>
        public static IReadOnlyList<string> ValueSetValues(Type type) =>
            [.. type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)];

        /// <summary>Whether a type gets a name in <c>$defs</c>.</summary>
        private static bool IsNamed(Type type) =>
            type.IsEnum || (Shape(type) == null && !IsDynamic(type) && !IsScalar(type));

        /// <summary>Collects every named type <paramref name="type"/> reaches.</summary>
        private void Reach(Type type, HashSet<Type> reached)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (Shape(type) is { } shape)
            {
                foreach (var inner in shape)
                {
                    Reach(inner, reached);
                }
                return;
            }
            if (!IsNamed(type) || !reached.Add(type))
            {
                return;
            }
            foreach (var derived in DerivedTypes(type))
            {
                Reach(derived, reached);
            }
            foreach (var property in Properties(type).Where(p => p.GetCustomAttribute<OutputSchemaAttribute>() is null))
            {
                Reach(property.PropertyType, reached);
                if (property.GetCustomAttribute<OutputValuesAttribute>() is { } values)
                {
                    Reach(values.Values, reached);
                }
            }
        }

        /// <summary>The element types of an array or dictionary (the value type); null for anything else.</summary>
        private static Type[]? Shape(Type type) =>
            DictionaryValue(type) is { } value ? [value] : Element(type) is { } element ? [element] : null;

        private static bool IsScalar(Type type) =>
            type == typeof(string) || type.IsPrimitive || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime)
            || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Uri);

        /// <summary>Values whose shape the type doesn't say: property values, error details, SQL cells.</summary>
        public static bool IsDynamic(Type type) =>
            type == typeof(object) || typeof(JsonNode).IsAssignableFrom(type) || type == typeof(JsonElement) || type == typeof(JsonDocument);

        /// <summary>The cases of a polymorphic base (<see cref="JsonDerivedTypeAttribute"/>), written as the case they are.</summary>
        public static IReadOnlyList<Type> DerivedTypes(Type type) =>
            [.. type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).Select(a => a.DerivedType)];

        /// <summary>
        /// The properties System.Text.Json writes, in its order: the most derived type's first, then its base types', then
        /// by <see cref="JsonPropertyOrderAttribute"/>.
        /// </summary>
        public static IReadOnlyList<PropertyInfo> Properties(Type type)
        {
            var found = new List<PropertyInfo>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            {
                foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (property.GetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0
                        || property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }
                        || !names.Add(property.Name))
                    {
                        continue;
                    }
                    found.Add(property);
                }
            }
            return [.. found.OrderBy(p => p.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0)];
        }

        public static string JsonName(PropertyInfo property) =>
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

        /// <summary>The schema of a value of <paramref name="type"/>, with <paramref name="info"/> for the nullability of what it holds.</summary>
        public JsonNode Schema(Type type, NullabilityInfo? info)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(string) || underlying == typeof(char))
            {
                return Simple("string");
            }
            if (underlying == typeof(bool))
            {
                return Simple("boolean");
            }
            if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short) || underlying == typeof(byte)
                || underlying == typeof(uint) || underlying == typeof(ulong) || underlying == typeof(ushort) || underlying == typeof(sbyte))
            {
                return Simple("integer");
            }
            if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
            {
                return Simple("number");
            }
            if (underlying == typeof(Guid))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
            }
            if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
            }
            if (underlying == typeof(DateOnly))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "date" };
            }
            if (underlying == typeof(TimeSpan) || underlying == typeof(TimeOnly))
            {
                // System.Text.Json writes both as [-][d.]hh:mm:ss[.fffffff], not as an ISO 8601 duration.
                return new JsonObject { ["type"] = "string", ["pattern"] = @"^-?(\d+\.)?\d{2}:\d{2}:\d{2}(\.\d{1,7})?$" };
            }
            if (underlying == typeof(Uri))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "uri-reference" };
            }
            if (underlying == typeof(JsonObject))
            {
                return new JsonObject { ["type"] = "object", ["x-dynamic"] = true };
            }
            if (underlying == typeof(JsonArray))
            {
                return new JsonObject { ["type"] = "array", ["x-dynamic"] = true };
            }
            if (IsDynamic(underlying))
            {
                return new JsonObject { ["x-dynamic"] = true };
            }
            if (DictionaryValue(underlying) is { } valueType)
            {
                return new JsonObject { ["type"] = "object", ["additionalProperties"] = Item(valueType, info?.GenericTypeArguments.ElementAtOrDefault(1)) };
            }
            if (Element(underlying) is { } elementType)
            {
                return new JsonObject { ["type"] = "array", ["items"] = Item(elementType, info?.ElementType ?? info?.GenericTypeArguments.FirstOrDefault()) };
            }
            Define(underlying);
            return new JsonObject { ["$ref"] = $"#/$defs/{_names[underlying]}" };
        }

        /// <summary>An array item or dictionary value: nulls in them are written (only properties are left out).</summary>
        private JsonNode Item(Type type, NullabilityInfo? info) =>
            info is { ReadState: NullabilityState.Nullable } || Nullable.GetUnderlyingType(type) is not null
                ? new JsonObject { ["anyOf"] = new JsonArray(Schema(type, info), Simple("null")) }
                : Schema(type, info);

        private static JsonObject Simple(string type) => new() { ["type"] = type };

        private static Type? DictionaryValue(Type type) =>
            type.GetInterfaces().Append(type)
                .FirstOrDefault(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
                ?.GetGenericArguments()[1];

        private static Type? Element(Type type) =>
            type == typeof(string) || typeof(JsonNode).IsAssignableFrom(type) ? null
            : type.IsArray ? type.GetElementType()
            : type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0]
            ?? (typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null);

        private void Define(Type type)
        {
            if (!_defined.Add(type))
            {
                return;
            }
            var definition = new JsonObject();
            Definitions[_names[type]] = definition;
            if (_docs.Summary(type) is { } summary)
            {
                definition["description"] = summary;
            }
            definition["x-csharp"] = type.FullName?.Replace('+', '.');
            if (type.IsEnum || IsValueSet(type))
            {
                definition["type"] = "string";
                definition["enum"] = new JsonArray([.. (type.IsEnum ? EnumNames(type) : ValueSetValues(type)).Select(n => (JsonNode)n)]);
                return;
            }
            if (DerivedTypes(type) is { Count: > 0 } cases)
            {
                definition["anyOf"] = new JsonArray([.. cases.Select(c => Schema(c, null))]);
                return;
            }
            definition["type"] = "object";
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var property in Properties(type))
            {
                var name = JsonName(property);
                var info = _nullability.Create(property);
                var schema = property.GetCustomAttribute<OutputSchemaAttribute>() is { } stated
                    ? JsonNode.Parse(stated.Json)!
                    : property.GetCustomAttribute<OutputValuesAttribute>() is { } values
                    ? ValuesSchema(property, values)
                    : Schema(property.PropertyType, info);
                if (_docs.Property(property.DeclaringType ?? type, property.Name) is { } doc && schema is JsonObject withDoc)
                {
                    // Next to a $ref, a description is fine in 2020-12; the referenced type keeps its own.
                    withDoc["description"] = doc;
                }
                properties[name] = schema;
                // Nulls are left out (WhenWritingNull), and defaults where the property says so: both may be absent.
                var ignore = property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition;
                if (info.ReadState == NullabilityState.NotNull && Nullable.GetUnderlyingType(property.PropertyType) is null
                    && ignore is not (JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault))
                {
                    required.Add(name);
                }
            }
            definition["properties"] = properties;
            definition["required"] = required;
            definition["additionalProperties"] = false;
        }

        /// <summary>
        /// A string property's known values (<see cref="OutputValuesAttribute"/>): the value set's <c>$ref</c>, or for an open
        /// set, that or any other string.
        /// </summary>
        private JsonObject ValuesSchema(PropertyInfo property, OutputValuesAttribute values)
        {
            if (property.PropertyType != typeof(string))
            {
                throw new InvalidOperationException($"{property.DeclaringType?.Name}.{property.Name}: [OutputValues] is for string properties.");
            }
            if (!values.Values.IsEnum && !IsValueSet(values.Values))
            {
                throw new InvalidOperationException($"{property.DeclaringType?.Name}.{property.Name}: [OutputValues] names {values.Values.Name}, which is neither an enum nor a static class of string constants.");
            }
            var known = Schema(values.Values, null);
            return values.Open ? new JsonObject { ["anyOf"] = new JsonArray(known, Simple("string")) } : known.AsObject();
        }

        /// <summary>An enum's values as <see cref="JsonStringEnumConverter"/> writes them with <see cref="JsonNamingPolicy.CamelCase"/>.</summary>
        public static IEnumerable<string> EnumNames(Type type) =>
            type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(f.Name));
    }

    /// <summary>The XML doc comments of the assemblies output types come from (OptiCli.Core.xml, opticli.xml), as plain text.</summary>
    internal sealed partial class XmlDocs
    {
        private readonly Dictionary<string, XElement> _members = [];

        public static XmlDocs Load()
        {
            var docs = new XmlDocs();
            foreach (var assembly in new[] { typeof(JsonOutput).Assembly, typeof(SchemaCommand).Assembly })
            {
                var file = Path.ChangeExtension(assembly.Location, ".xml");
                if (!File.Exists(file))
                {
                    continue;
                }
                foreach (var member in XDocument.Load(file).Descendants("member"))
                {
                    if (member.Attribute("name")?.Value is { } name)
                    {
                        docs._members[name] = member;
                    }
                }
            }
            return docs;
        }

        private static string Id(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

        public string? Summary(Type type) =>
            _members.TryGetValue("T:" + Id(type), out var member) && member.Element("summary") is { } summary ? Text(summary) : null;

        /// <summary>The property's own summary, else the record's <c>param</c> of that name (positional records).</summary>
        public string? Property(Type type, string name)
        {
            if (_members.TryGetValue($"P:{Id(type)}.{name}", out var member) && member.Element("summary") is { } summary)
            {
                return Text(summary);
            }
            return _members.TryGetValue("T:" + Id(type), out var typeMember)
                && typeMember.Elements("param").FirstOrDefault(p => p.Attribute("name")?.Value == name) is { } param
                ? Text(param)
                : null;
        }

        private static string Text(XElement element) => Whitespace().Replace(string.Concat(element.Nodes().Select(Node)), " ").Trim();

        private static string Node(XNode node) => node switch
        {
            XText text => text.Value,
            XElement { Name.LocalName: "c" } code => $"`{code.Value}`",
            XElement { Name.LocalName: "see" or "seealso" } see when see.Attribute("langword") is { } word => $"`{word.Value}`",
            XElement { Name.LocalName: "see" or "seealso" } see when see.Attribute("cref") is { } cref => $"`{Cref(cref.Value)}`",
            XElement { Name.LocalName: "paramref" or "typeparamref" } reference => $"`{reference.Attribute("name")?.Value}`",
            XElement element => string.Concat(element.Nodes().Select(Node)),
            _ => "",
        };

        /// <summary>A property or field as its JSON name (<c>language</c>), a type or method by its name.</summary>
        private static string Cref(string cref)
        {
            var name = cref.Split('(')[0];
            name = name[(name.LastIndexOf('.') + 1)..];
            if (name.Contains('`', StringComparison.Ordinal))
            {
                name = name[..name.IndexOf('`', StringComparison.Ordinal)];
            }
            return cref.StartsWith("P:", StringComparison.Ordinal) || cref.StartsWith("F:", StringComparison.Ordinal) ? JsonNamingPolicy.CamelCase.ConvertName(name) : name;
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();
    }
}
