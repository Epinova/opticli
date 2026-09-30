using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.SpecializedProperties;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Content;

/// <summary>Applies <see cref="AreaOperation"/>s (add/remove/move) to ContentArea properties.</summary>
internal sealed class AreaEditor(ContentLocator locator, PropertyWriter writer)
{
    public void Apply(IContentData content, IReadOnlyList<AreaOperation>? operations)
    {
        if (operations is null)
        {
            return;
        }
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            var property = PropertyWriter.Find(content, op.Property);
            if (property is not PropertyContentArea)
            {
                throw AgentException.Usage($"areaOps[{i}]: '{property.Name}' is a {property.GetType().Name}, not a ContentArea.");
            }

            // Work on a copy and assign it back, so the property registers the change.
            var area = property.Value is ContentArea existing ? (ContentArea)((IReadOnly)existing).CreateWritableClone() : new ContentArea();
            var items = area.Items;
            var where = $"areaOps[{i}] ({op.Op} on {property.Name})";

            switch (op.Op)
            {
                case AreaOps.Add:
                    if (op.IfMissing && op.Ref is not null && items.Any(item => item.ContentLink is { } present && present.CompareToIgnoreWorkID(locator.ResolveContent(op.Ref, "ContentArea item"))))
                    {
                        continue;
                    }
                    var at = op.At ?? items.Count;
                    CheckPosition(where, at, items.Count);
                    items.Insert(at, writer.NewAreaItem(op.Ref, op.DisplayOption));
                    break;

                case AreaOps.Remove:
                    items.RemoveAt(Locate(where, items, op));
                    break;

                case AreaOps.Move:
                    var from = Locate(where, items, op);
                    var to = op.At ?? throw AgentException.Usage($"{where}: 'at' (the target position) is required.");
                    CheckPosition(where, to, items.Count - 1);
                    var item = items[from];
                    items.RemoveAt(from);
                    items.Insert(to, item);
                    break;

                default:
                    throw AgentException.Usage($"areaOps[{i}]: unknown op '{op.Op}'.", $"Use {AreaOps.Add}, {AreaOps.Remove} or {AreaOps.Move}.");
            }

            property.Value = area;
        }
    }

    /// <summary>The item an op refers to: by index, or the first item referencing its ref.</summary>
    private int Locate(string where, IList<ContentAreaItem> items, AreaOperation op)
    {
        if (op.Index is { } index)
        {
            CheckPosition(where, index, items.Count - 1);
            return index;
        }
        if (op.Ref is null)
        {
            throw AgentException.Usage($"{where}: give 'index' or 'ref' of the item.");
        }
        var target = locator.ResolveContent(op.Ref, "ContentArea item");
        var found = items.ToList().FindIndex(item => item.ContentLink is { } link && link.CompareToIgnoreWorkID(target));
        return found >= 0
            ? found
            : throw AgentException.NotFound($"{where}: no item references {op.Ref}.",
                $"Items: {string.Join(", ", items.Select(item => item.ContentLink?.ToString() ?? "(inline)"))}.");
    }

    private static void CheckPosition(string where, int position, int max)
    {
        if (max < 0)
        {
            throw AgentException.Usage($"{where}: the ContentArea is empty.");
        }
        if (position < 0 || position > max)
        {
            throw AgentException.Usage($"{where}: position {position} is out of range (0..{max}).");
        }
    }
}
