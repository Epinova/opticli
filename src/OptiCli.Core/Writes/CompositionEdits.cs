namespace OptiCli.Core.Writes;

/// <summary>The rules of a <see cref="CompositionEdit"/>'s shape, for the command and plans alike.</summary>
public static class CompositionEdits
{
    public static readonly IReadOnlyList<string> Actions = ["add", "remove", "move", "set"];

    public static readonly IReadOnlyList<string> NodeTypes = ["section", "row", "column", "element"];

    public const string Syntax =
        "add <section|row|column|element> [--in <node>] [--at n] (--type T | --ref R | --blueprint B | --node JSON); remove <node>; move <node> [--in <node>] [--at n]; set <node> [Prop=value] [--name] [--template] [--setting k=v]. A node is its key, or its name if only one node has it; root is the composition itself.";

    /// <returns>What is wrong with the edit's fields; null when they fit its action.</returns>
    public static string? Problem(CompositionEdit edit)
    {
        if (!Actions.Contains(edit.Action))
        {
            return "\"action\" must be add, remove, move or set.";
        }
        if (edit.NodeType is { } nodeType && (edit.Action != "add" || !NodeTypes.Contains(nodeType.Trim().ToLowerInvariant()) && nodeType.Trim().ToLowerInvariant() != "component"))
        {
            return edit.Action == "add" ? $"the node type '{nodeType}' must be section, row, column or element." : $"{edit.Action} takes no node type; it is the node's.";
        }
        return edit.Action switch
        {
            "add" when edit.Node is not null => "add makes a new node; name the node to put it in with \"in\" (--in), not \"node\".",
            "add" when edit.Value is null => "add needs the node to add: its type, ref or blueprint (a plan's \"value\").",
            "remove" when edit.Node is null => "remove needs the node: its key, or its name.",
            "remove" when edit.Value is not null || edit.Parent is not null || edit.At is not null => "remove takes only the node.",
            "move" when edit.Node is null => "move needs the node: its key, or its name.",
            "move" when edit.Value is not null => "move takes the node, and where to (in, at); change it with set.",
            "move" when edit.Parent is null && edit.At is null => "move needs where to: in (another parent) or at (a position).",
            "set" when edit.Parent is not null || edit.At is not null => "set changes a node where it is; move it with move.",
            "set" when edit.Value is null or { Count: 0 } => "set needs what to change: properties, name, a display template or settings.",
            _ => null,
        };
    }
}
