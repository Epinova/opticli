namespace OptiCli.Core.Properties;

/// <summary>A property and, for block-typed ones, the values nested inside it.</summary>
public sealed class PropertyNode(int definitionId)
{
    public int DefinitionId { get; } = definitionId;

    /// <summary>The stored value; null for block properties, which only have nested values.</summary>
    public PropertyRow? Row { get; set; }

    /// <summary>Properties of a single local block, by definition id.</summary>
    public Dictionary<int, PropertyNode> Properties { get; } = [];

    /// <summary>Items of a block list, or inline blocks of a ContentArea, by position.</summary>
    public SortedDictionary<int, BlockItem> Items { get; } = [];

    /// <summary>Items of a list of plain values (<c>IList&lt;ContentReference&gt;</c>, ...), by position; one row each.</summary>
    public SortedDictionary<int, PropertyRow> Values { get; } = [];

    public IEnumerable<PropertyRow> AllRows() =>
        (Row is null ? [] : new[] { Row })
            .Concat(Values.Values)
            .Concat(Properties.Values.SelectMany(p => p.AllRows()))
            .Concat(Items.Values.SelectMany(i => i.Properties.Values.SelectMany(p => p.AllRows())));
}

/// <param name="TypeId">Content type of an inline block, when the scope names it.</param>
public sealed class BlockItem(int? typeId)
{
    public int? TypeId { get; set; } = typeId;

    public Dictionary<int, PropertyNode> Properties { get; } = [];
}

public static class PropertyTree
{
    /// <summary>Nests flat rows by their <see cref="ScopePath"/>; rows with unreadable scopes are skipped.</summary>
    public static Dictionary<int, PropertyNode> Build(IEnumerable<PropertyRow> rows)
    {
        var top = new Dictionary<int, PropertyNode>();
        foreach (var row in rows)
        {
            // A top-level Category row's scope is its own definition id ("22"), which keys its categories.
            if (string.IsNullOrEmpty(row.ScopeName) || row.ScopeName == row.DefinitionId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                Node(top, row.DefinitionId).Row = row;
                continue;
            }

            if (ScopePath.Parse(row.ScopeName) is not { } scope)
            {
                continue;
            }

            var container = top;
            foreach (var step in scope.Steps)
            {
                var node = Node(container, step.PropertyId);
                if (step.Index is { } index)
                {
                    if (!node.Items.TryGetValue(index, out var item))
                    {
                        item = new BlockItem(step.InlineTypeId);
                        node.Items[index] = item;
                    }
                    item.TypeId ??= step.InlineTypeId;
                    container = item.Properties;
                }
                else
                {
                    container = node.Properties;
                }
            }
            var leaf = Node(container, row.DefinitionId);
            if (scope.LeafIndex is { } position)
            {
                leaf.Values[position] = row;
            }
            else
            {
                leaf.Row = row;
            }
        }
        return top;
    }

    private static PropertyNode Node(Dictionary<int, PropertyNode> container, int definitionId)
    {
        if (!container.TryGetValue(definitionId, out var node))
        {
            node = new PropertyNode(definitionId);
            container[definitionId] = node;
        }
        return node;
    }
}
