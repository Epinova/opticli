using System.Globalization;
using System.Text.Json;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// One node of a Visual Builder composition (CMS 13) while it is edited: the CMS's composition nodes are read-only and
/// mostly init-only, so an edit works on these and the CMS adapter (<c>Compat/CmsCompositions</c>) turns them back into
/// the CMS's nodes, which its own mapper stores.
/// </summary>
internal sealed class DraftNode
{
    public const string Experience = "experience";
    public const string Section = "section";
    public const string Row = "row";
    public const string Column = "column";
    public const string Component = "component";

    /// <summary><see cref="Experience"/> (the root of an experience), <see cref="Section"/>, <see cref="Row"/>, <see cref="Column"/> or <see cref="Component"/>.</summary>
    public required string NodeType { get; set; }

    public string? Key { get; set; }

    public string? Name { get; set; }

    /// <summary>The block a section or component shows; null for the root, rows and columns.</summary>
    public CompositionBlock? Block { get; set; }

    public string? DisplayTemplate { get; set; }

    public Dictionary<string, string> DisplaySettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<DraftNode> Children { get; } = [];

    /// <summary>Whether it can hold rows: a section with an inline block (a shared section's grid is the shared block's).</summary>
    public bool HasGrid => NodeType == Section && Block is not { Shared: true };

    /// <summary>"section "Hero" (c2f9…)" for messages.</summary>
    public string Describe() =>
        $"{(NodeType == Component ? "element" : NodeType)}{(Name is { Length: > 0 } ? $" \"{Name}\"" : "")}{(Key is null ? " (new)" : $" ({Key})")}";
}

/// <summary>A block a composition node shows, as the editor needs to know it.</summary>
/// <param name="Block">The CMS's block (a <c>BlockData</c>); the editor only passes it on.</param>
/// <param name="Type">Its content type's name.</param>
/// <param name="Shared">Shared content placed by reference, rather than an inline block of the content.</param>
/// <param name="Ref">For a shared block, its content id.</param>
/// <param name="IsSection">A section type (it has a grid of its own).</param>
/// <param name="SectionEnabled">It may stand in an experience's outline.</param>
/// <param name="ElementEnabled">It may stand in a section's columns.</param>
internal sealed record CompositionBlock(object Block, string Type, bool Shared, string? Ref, bool IsSection, bool SectionEnabled, bool ElementEnabled);

/// <summary>What the editor needs from the CMS: blocks, their values and the display templates.</summary>
internal interface ICompositionBlocks
{
    /// <summary>A new inline block of the type (name or GUID).</summary>
    /// <exception cref="AgentException"><c>usage</c> for a type that isn't a block type.</exception>
    CompositionBlock New(string type, string where);

    /// <summary>A shared block, placed by reference.</summary>
    /// <exception cref="AgentException"><c>not_found</c>, or <c>usage</c> for content that isn't a block.</exception>
    CompositionBlock Shared(string reference, string where);

    /// <summary>A new inline section copied from a section blueprint, and its grid (its rows, with new keys).</summary>
    (CompositionBlock Block, IReadOnlyList<DraftNode> Rows, string? DisplayTemplate, IReadOnlyDictionary<string, string> DisplaySettings) Blueprint(Guid blueprint, string where);

    /// <summary>A copy of an inline block that may be changed.</summary>
    CompositionBlock Writable(CompositionBlock block);

    /// <summary>Sets an inline block's properties, as a draft's properties are set.</summary>
    void SetProperties(CompositionBlock block, IReadOnlyDictionary<string, JsonElement> properties, string where);

    /// <summary>The name a new section or element gets when none is given: its type's display name.</summary>
    string DefaultName(CompositionBlock block);

    /// <summary>Checks a node's display template and settings against the site's display templates.</summary>
    /// <exception cref="AgentException"><c>usage</c> naming what is wrong and what the site has.</exception>
    void CheckStyle(DraftNode node, string where);
}

/// <summary>
/// Applies a request's Visual Builder composition (<see cref="DraftRequest.Composition"/>) and edits
/// (<see cref="DraftRequest.CompositionOps"/>) to a composition, with the CMS's structure rules checked up front so a
/// mistake is named in opticli's terms: an outline holds sections and section-enabled blocks, a section rows, a row
/// columns, a column element-enabled blocks. Keys stay as they are; new nodes get new ones.
/// </summary>
internal sealed class CompositionEditor(DraftNode root, ICompositionBlocks blocks, Func<string>? newKey = null)
{
    private readonly Func<string> _newKey = newKey ?? (() => Guid.NewGuid().ToString());

    public DraftNode Root => root;

    /// <summary>Replaces the whole composition with <paramref name="value"/> (see <see cref="CompositionNodeValue"/>).</summary>
    public void Replace(CompositionNodeValue value)
    {
        const string where = "composition";
        if (value.Type is not null || value.Ref is not null || value.Blueprint is not null || value.Properties is not null)
        {
            throw AgentException.Usage($"{where}: the composition itself has no type, ref, blueprint or properties; they belong to its sections and elements.");
        }
        var existing = All(root).Where(n => n.Key is not null && n != root).ToDictionary(n => n.Key!, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var styleBefore = Style(root);
        root.DisplayTemplate = Empty(value.DisplayTemplate);
        root.DisplaySettings = Settings(value.DisplaySettings);
        // Like a node's: only a style the request changes is checked, not one carried over.
        if ((root.DisplayTemplate is not null || root.DisplaySettings.Count > 0) && styleBefore != Style(root))
        {
            blocks.CheckStyle(root, where);
        }
        var children = (value.Nodes ?? []).Select((child, i) => Rebuild(child, root, existing, used, Child(where, root, i))).ToList();
        root.Children.Clear();
        root.Children.AddRange(children);
    }

    /// <summary>Applies the edits in order.</summary>
    public void Apply(IReadOnlyList<CompositionOperation>? operations)
    {
        for (var i = 0; i < (operations?.Count ?? 0); i++)
        {
            var op = operations![i];
            // In the command's terms ("composition move"); a request with several edits also says which one.
            var where = operations.Count == 1 ? $"composition {op.Op}" : $"composition {op.Op} (edit {(i + 1).ToString(CultureInfo.InvariantCulture)} of {operations.Count.ToString(CultureInfo.InvariantCulture)})";
            switch (op.Op)
            {
                case CompositionOps.Add:
                    Add(op, where);
                    break;
                case CompositionOps.Remove:
                    Remove(op, where);
                    break;
                case CompositionOps.Move:
                    Move(op, where);
                    break;
                case CompositionOps.Set:
                    Set(op, where);
                    break;
                default:
                    throw AgentException.Usage($"composition: unknown edit '{op.Op}'.",
                        $"Use {CompositionOps.Add}, {CompositionOps.Remove}, {CompositionOps.Move} or {CompositionOps.Set}.");
            }
        }
    }

    private void Add(CompositionOperation op, string where)
    {
        var value = op.Value ?? throw AgentException.Usage($"{where}: give the node to add (value).");
        var parent = Find(op.Parent, where, "parent");
        var used = new HashSet<string>(All(root).Select(n => n.Key).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var node = Build(value, parent, used, where);
        var at = op.At ?? parent.Children.Count;
        CheckPosition(where, at, parent.Children.Count, parent);
        parent.Children.Insert(at, node);
    }

    private void Remove(CompositionOperation op, string where)
    {
        var node = Find(op.Node ?? throw AgentException.Usage($"{where}: give the node to remove (its key, or its name)."), where, "node");
        if (node == root)
        {
            throw AgentException.Usage($"{where}: the composition itself can't be removed.", "Remove its sections one by one, or write the whole composition with no sections.");
        }
        ParentOf(node).Children.Remove(node);
    }

    private void Move(CompositionOperation op, string where)
    {
        var node = Find(op.Node ?? throw AgentException.Usage($"{where}: give the node to move (its key, or its name)."), where, "node");
        if (node == root)
        {
            throw AgentException.Usage($"{where}: the composition itself can't be moved.");
        }
        var from = ParentOf(node);
        var to = op.Parent is null ? from : Find(op.Parent, where, "parent");
        if (to == node || All(node).Contains(to))
        {
            throw AgentException.Usage($"{where}: {node.Describe()} can't be moved into itself.");
        }
        Place(node, to, where);
        from.Children.Remove(node);
        var at = op.At ?? to.Children.Count;
        CheckPosition(where, at, to.Children.Count, to);
        to.Children.Insert(at, node);
    }

    private void Set(CompositionOperation op, string where)
    {
        var node = Find(op.Node, where, "node");
        var value = op.Value ?? throw AgentException.Usage($"{where}: give what to change (value: name, displayTemplate, displaySettings, properties).");
        if (value.Type is not null || value.Ref is not null || value.Blueprint is not null || value.Nodes is not null || value.Key is not null)
        {
            throw AgentException.Usage($"{where}: set changes a node's name, display template, display settings and properties, not its block, key or children.",
                "To put another block there, remove the node and add a new one; to change its children, add, move or remove them.");
        }
        if (value.NodeType is { } nodeType && Normalise(nodeType) != node.NodeType)
        {
            throw AgentException.Usage($"{where}: {node.Describe()} is {A(Shown(node.NodeType))}, not {A(nodeType)}.");
        }
        if (value.Name is not null)
        {
            if (node == root)
            {
                throw AgentException.Usage($"{where}: the composition has no name of its own; the content's name is set with name.");
            }
            node.Name = value.Name;
        }
        var styled = false;
        if (value.DisplayTemplate is not null)
        {
            node.DisplayTemplate = Empty(value.DisplayTemplate);
            if (node.DisplayTemplate is null)
            {
                // Settings belong to a template.
                node.DisplaySettings.Clear();
            }
            styled = true;
        }
        foreach (var (key, setting) in value.DisplaySettings ?? new Dictionary<string, string?>())
        {
            if (setting is null)
            {
                node.DisplaySettings.Remove(key);
            }
            else
            {
                node.DisplaySettings[key] = setting;
            }
            styled = true;
        }
        if (styled && (node.DisplayTemplate is not null || node.DisplaySettings.Count > 0))
        {
            blocks.CheckStyle(node, where);
        }
        if (value.Properties is { } properties)
        {
            SetProperties(node, properties, where);
        }
    }

    /// <summary>A new node (and its children) from <paramref name="value"/>, placed under <paramref name="parent"/>.</summary>
    private DraftNode Build(CompositionNodeValue value, DraftNode parent, HashSet<string> used, string where)
    {
        var nodeType = NodeTypeUnder(parent, value.NodeType, where);
        var node = new DraftNode { NodeType = nodeType, Key = NewKey(value.Key, used, where) };
        if (nodeType is DraftNode.Row or DraftNode.Column)
        {
            if (value.Type is not null || value.Ref is not null || value.Blueprint is not null || value.Properties is not null)
            {
                throw AgentException.Usage($"{where}: {A(nodeType)} has no block: give type, ref, blueprint and properties on the elements (or sections) in it.");
            }
        }
        else
        {
            var sources = new[] { value.Type is not null, value.Ref is not null, value.Blueprint is not null }.Count(s => s);
            if (sources != 1)
            {
                throw AgentException.Usage($"{where}: give exactly one of type (a new inline block), ref (a shared block) or blueprint (a section blueprint) for the new {Shown(nodeType)}.");
            }
            if (value.Blueprint is { } blueprint)
            {
                var (block, rows, template, settings) = blocks.Blueprint(blueprint, where);
                node.Block = block;
                node.DisplayTemplate = template;
                node.DisplaySettings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    Rekey(row, used);
                    node.Children.Add(row);
                }
            }
            else
            {
                node.Block = value.Ref is { } reference ? blocks.Shared(reference, where) : blocks.New(value.Type!, where);
            }
            node.NodeType = Fit(node, parent, where);
            if (node.Block.Shared && value.Properties is not null)
            {
                throw AgentException.Usage($"{where}: {node.Block.Type} {node.Block.Ref} is shared content, placed here by reference; its properties are its own.",
                    $"Change them with set {node.Block.Ref} (every placement shows the change), or add an inline block (type) instead.");
            }
            if (value.Properties is { } properties)
            {
                blocks.SetProperties(node.Block, properties, $"{where}: {node.Block.Type}");
            }
        }
        node.Name = value.Name ?? (node.Block is { } named ? blocks.DefaultName(named) : null);
        if (value.DisplayTemplate is not null || value.DisplaySettings is not null)
        {
            node.DisplayTemplate = Empty(value.DisplayTemplate) ?? node.DisplayTemplate;
            foreach (var (key, setting) in value.DisplaySettings ?? new Dictionary<string, string?>())
            {
                if (setting is not null)
                {
                    node.DisplaySettings[key] = setting;
                }
            }
            blocks.CheckStyle(node, where);
        }
        var children = value.Nodes ?? [];
        if (children.Count > 0 && value.Blueprint is not null)
        {
            throw AgentException.Usage($"{where}: a section from a blueprint has the blueprint's rows; add more once it is there.");
        }
        for (var i = 0; i < children.Count; i++)
        {
            node.Children.Add(Build(children[i], node, used, Child(where, node, i)));
        }
        return node;
    }

    /// <summary>For <see cref="Replace"/>: the node <paramref name="value"/> describes, the existing one with its key or a new one.</summary>
    private DraftNode Rebuild(CompositionNodeValue value, DraftNode parent, IReadOnlyDictionary<string, DraftNode> existing, HashSet<string> used, string where)
    {
        if (value.Key is null)
        {
            var allKeys = new HashSet<string>(existing.Keys.Concat(used), StringComparer.OrdinalIgnoreCase);
            var fresh = Build(value, parent, allKeys, where);
            used.UnionWith(All(fresh).Select(n => n.Key).OfType<string>());
            return fresh;
        }
        if (!existing.TryGetValue(value.Key, out var node))
        {
            throw AgentException.Usage($"{where}: no node of the composition has the key {value.Key}.",
                "Keys are what get shows; leave key out for a new node.");
        }
        if (!used.Add(node.Key!))
        {
            throw AgentException.Usage($"{where}: the key {value.Key} is given twice.", "Each node appears once; leave key out for a new node.");
        }
        if (value.NodeType is { } nodeType && Normalise(nodeType) is var normal && normal != node.NodeType
            && !(normal is DraftNode.Section or DraftNode.Component && node.NodeType is DraftNode.Section or DraftNode.Component))
        {
            throw AgentException.Usage($"{where}: {node.Describe()} is {A(Shown(node.NodeType))}, not {A(nodeType)}.");
        }
        if (value.Blueprint is not null)
        {
            throw AgentException.Usage($"{where}: a blueprint makes a new section; leave key out to add one.");
        }
        if (node.Block is { } block)
        {
            if (value.Type is { } type && !type.Equals(block.Type, StringComparison.OrdinalIgnoreCase))
            {
                throw AgentException.Usage($"{where}: {node.Describe()} is {A(block.Type)}, not {A(type)}; a node keeps its block.",
                    "Leave key out to put a new block of another type there.");
            }
            if (value.Ref is { } reference)
            {
                if (!block.Shared)
                {
                    throw AgentException.Usage($"{where}: {node.Describe()} is an inline block, so it has no ref.", "Leave key out to place a shared block there instead.");
                }
                var shared = blocks.Shared(reference, where);
                if (!string.Equals(shared.Ref, block.Ref, StringComparison.Ordinal))
                {
                    node.Block = shared;
                }
            }
            if (value.Properties is { } properties)
            {
                SetProperties(node, properties, where);
            }
        }
        else if (value.Type is not null || value.Ref is not null || value.Properties is not null)
        {
            throw AgentException.Usage($"{where}: {A(Shown(node.NodeType))} has no block: give type, ref and properties on the elements (or sections) in it.");
        }
        node.Name = value.Name ?? node.Name;
        var styleBefore = Style(node);
        node.DisplayTemplate = Empty(value.DisplayTemplate);
        node.DisplaySettings = Settings(value.DisplaySettings);
        if ((node.DisplayTemplate is not null || node.DisplaySettings.Count > 0) && styleBefore != Style(node))
        {
            blocks.CheckStyle(node, where);
        }
        var children = (value.Nodes ?? []).ToList();
        if (children.Count > 0 && !(node.NodeType is DraftNode.Row or DraftNode.Column || node.HasGrid))
        {
            throw AgentException.Usage($"{where}: {node.Describe()} can't hold other nodes{(node.Block is { Shared: true } ? " (a shared section's rows are the shared block's own)" : "")}.");
        }
        var rebuilt = children.Select((child, i) => Rebuild(child, node, existing, used, Child(where, node, i))).ToList();
        Place(node, parent, where);
        node.Children.Clear();
        node.Children.AddRange(rebuilt);
        return node;
    }

    private void SetProperties(DraftNode node, IReadOnlyDictionary<string, JsonElement> properties, string where)
    {
        if (node.Block is not { } block)
        {
            throw AgentException.Usage($"{where}: {node.Describe()} has no properties of its own; its sections and elements do.");
        }
        if (block.Shared)
        {
            throw AgentException.Usage($"{where}: {node.Describe()} is shared content ({block.Type} {block.Ref}), placed here by reference; its properties are its own.",
                $"Change them with set {block.Ref} (every placement shows the change).");
        }
        node.Block = blocks.Writable(block);
        blocks.SetProperties(node.Block, properties, $"{where}: {node.Describe()}");
    }

    /// <summary>The node type a new child of <paramref name="parent"/> has: the one given, or the only one the parent takes.</summary>
    private static string NodeTypeUnder(DraftNode parent, string? given, string where)
    {
        var expected = parent.NodeType switch
        {
            DraftNode.Experience => DraftNode.Section,
            DraftNode.Section when parent.HasGrid => DraftNode.Row,
            DraftNode.Row => DraftNode.Column,
            DraftNode.Column => DraftNode.Component,
            _ => null,
        } ?? throw AgentException.Usage($"{where}: {parent.Describe()} can't hold other nodes{(parent.Block is { Shared: true } ? " (a shared section's rows are the shared block's own: change them on it)" : "")}.",
            "An experience holds sections, a section rows, a row columns, a column elements.");
        if (given is null)
        {
            return expected;
        }
        var normal = Normalise(given);
        // In an outline a section-enabled block is a component; the block decides (Fit).
        if (normal == expected || (expected == DraftNode.Section && normal == DraftNode.Component))
        {
            return expected;
        }
        throw AgentException.Usage($"{where}: {A(Shown(normal))} can't go in {parent.Describe()}: {Shown(parent.NodeType)}s hold {Shown(expected)}s.",
            "An experience holds sections, a section rows, a row columns, a column elements.");
    }

    /// <summary>A section or component's node type under <paramref name="parent"/>, from its block; refused where the CMS refuses it.</summary>
    private static string Fit(DraftNode node, DraftNode parent, string where)
    {
        var block = node.Block!;
        if (parent.NodeType == DraftNode.Experience)
        {
            if (!block.SectionEnabled && !block.IsSection)
            {
                throw AgentException.Usage($"{where}: {block.Type} can't stand in an experience's outline: only section types and blocks with the SectionEnabled composition behaviour can.",
                    "`opticli types --kind section` lists section types; `opticli allowed-in <type>` says where a type can go.");
            }
            return block.IsSection ? DraftNode.Section : DraftNode.Component;
        }
        if (parent.NodeType == DraftNode.Column)
        {
            if (block.IsSection || !block.ElementEnabled)
            {
                throw AgentException.Usage($"{where}: {block.Type} can't be placed in a column: only element types (the ElementEnabled composition behaviour) can{(block.IsSection ? ", and a section can't be inside another" : "")}.",
                    "`opticli types --kind element` lists element types.");
            }
            return DraftNode.Component;
        }
        throw AgentException.Usage($"{where}: {parent.Describe()} can't hold {block.Type}.");
    }

    /// <summary>Checks that <paramref name="node"/> may stand under <paramref name="parent"/> (for a move).</summary>
    private static void Place(DraftNode node, DraftNode parent, string where)
    {
        if (node.Block is not null)
        {
            node.NodeType = Fit(node, parent, where);
            return;
        }
        var expected = NodeTypeUnder(parent, null, where);
        if (expected != node.NodeType)
        {
            throw AgentException.Usage($"{where}: {node.Describe()} can't go in {parent.Describe()}: {Shown(parent.NodeType)}s hold {Shown(expected)}s.");
        }
    }

    /// <summary>A node by key (any case), by <c>root</c> (or nothing), or by name when only one node has it.</summary>
    public DraftNode Find(string? reference, string where, string what)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Trim().Equals(CompositionOps.Root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }
        var text = reference.Trim();
        var nodes = All(root).Where(n => n != root).ToList();
        if (nodes.FirstOrDefault(n => string.Equals(n.Key, text, StringComparison.OrdinalIgnoreCase)) is { } byKey)
        {
            return byKey;
        }
        var byName = nodes.Where(n => string.Equals(n.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1)
        {
            return byName[0];
        }
        if (byName.Count > 1)
        {
            throw AgentException.Usage($"{where}: {byName.Count} nodes are named '{text}': {string.Join(", ", byName.Select(n => $"{n.Describe()} in {ParentOf(n).Describe()}"))}.",
                $"Name the {what} by its key.");
        }
        const int MaxListed = 25;
        var listed = string.Join(", ", nodes.Take(MaxListed).Select(n => n.Describe()));
        throw AgentException.NotFound($"{where}: the composition has no node with the key or name '{text}'.",
            nodes.Count == 0 ? "It has no sections yet; add one to the composition itself (root)." : $"Nodes: {listed}{(nodes.Count > MaxListed ? $", and {nodes.Count - MaxListed} more" : "")} (opticli get <ref> shows them all).");
    }

    private DraftNode ParentOf(DraftNode node) =>
        All(root).FirstOrDefault(n => n.Children.Contains(node)) ?? throw new InvalidOperationException("A node without a parent.");

    /// <summary>The node and everything below it.</summary>
    public static IEnumerable<DraftNode> All(DraftNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var below in All(child))
            {
                yield return below;
            }
        }
    }

    private string NewKey(string? given, HashSet<string> used, string where)
    {
        if (given is null)
        {
            string key;
            do
            {
                key = _newKey();
            }
            while (!used.Add(key));
            return key;
        }
        if (!Guid.TryParse(given, out var guid))
        {
            throw AgentException.Usage($"{where}: the key '{given}' of a new node isn't a GUID.", "Leave key out and a new one is made (the result shows it).");
        }
        var normal = guid.ToString();
        return used.Add(normal)
            ? normal
            : throw AgentException.Usage($"{where}: the composition already has a node with the key {given}.", "Leave key out for a new node.");
    }

    /// <summary>New keys for a blueprint's nodes, which every copy of it would otherwise share.</summary>
    private void Rekey(DraftNode node, HashSet<string> used)
    {
        foreach (var each in All(node))
        {
            each.Key = NewKey(null, used, "");
        }
    }

    private static void CheckPosition(string where, int at, int count, DraftNode parent)
    {
        if (at < 0 || at > count)
        {
            throw AgentException.Usage($"{where}: position {at.ToString(CultureInfo.InvariantCulture)} is out of range (0..{count.ToString(CultureInfo.InvariantCulture)}) in {parent.Describe()}.");
        }
    }

    /// <summary><c>element</c> as the CMS's <c>component</c>; other node types as given, lower case.</summary>
    public static string Normalise(string nodeType) => nodeType.Trim().ToLowerInvariant() switch
    {
        "element" or "component" => DraftNode.Component,
        var other => other,
    };

    private static string Shown(string nodeType) => nodeType == DraftNode.Component ? "element" : nodeType;

    /// <summary>
    /// Where a child is, named after the list <c>get</c> shows it in (<c>sections</c>, <c>rows</c>, <c>columns</c>,
    /// <c>elements</c>), so an error points at the input as it was written.
    /// </summary>
    private static string Child(string where, DraftNode parent, int index) =>
        $"{where}.{parent.NodeType switch
        {
            DraftNode.Experience => "sections",
            DraftNode.Section => "rows",
            DraftNode.Row => "columns",
            DraftNode.Column => "elements",
            _ => "nodes",
        }}[{index.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>"a section", "an element".</summary>
    private static string A(string noun) => (noun.Length > 0 && "aeiouAEIOU".Contains(noun[0]) ? "an " : "a ") + noun;

    /// <summary>A node's display template and settings, to see whether a request changed them.</summary>
    private static (string? Template, string Settings) Style(DraftNode node) =>
        (node.DisplayTemplate, string.Join(";", node.DisplaySettings.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => $"{s.Key}={s.Value}")));

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Dictionary<string, string> Settings(IReadOnlyDictionary<string, string?>? given) =>
        (given ?? new Dictionary<string, string?>()).Where(s => s.Value is not null).ToDictionary(s => s.Key, s => s.Value!, StringComparer.OrdinalIgnoreCase);
}
