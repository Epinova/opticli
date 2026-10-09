using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.SpecializedProperties;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

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
            PropertyData property;
            Action commit;
            try
            {
                property = writer.FindArea(content, op.Property, out commit);
            }
            catch (AgentException ex) when (ex.Code == AgentErrorCodes.Usage)
            {
                throw new AgentException(ex.Code, $"areaOps[{i}]: {ex.Message}", ex.Hint);
            }
            // A nested area by its path (MainArea[0].Area), else the property's own name.
            var name = op.Property.Contains('.') ? op.Property : property.Name;

            // Work on a copy and assign it back, so the property registers the change.
            var area = property.Value is ContentArea existing ? (ContentArea)((IReadOnly)existing).CreateWritableClone() : new ContentArea();
            var items = area.Items;
            var where = $"areaOps[{i}] ({op.Op} on {name})";

            switch (op.Op)
            {
                case AreaOps.Add:
                    var inline = op.Type is not null || op.Values is not null || op.Name is not null;
                    if (inline && op.Ref is not null)
                    {
                        throw AgentException.Usage($"{where}: give ref (a shared block) or type (a new inline block), not both.");
                    }
                    if (inline && op.Type is null)
                    {
                        throw AgentException.Usage($"{where}: values and name are a new inline block's; give its type too.");
                    }
                    if (!inline && op.Ref is null)
                    {
                        throw AgentException.Usage($"{where}: give ref (a shared block to add) or type (a new inline block, with its values).");
                    }
                    ContentAreaItem added;
                    if (inline)
                    {
                        // An inline block has no identity: "already there" is one of its type with the values given.
                        if (writer.NewInlineItem(op.Type!, op.Values, op.Name, op.DisplayOption, where, op.IfMissing ? items : null) is not { } inlineItem)
                        {
                            continue;
                        }
                        added = inlineItem;
                    }
                    else
                    {
                        if (op.IfMissing && items.Any(item => item.ContentLink is { } present && present.CompareToIgnoreWorkID(locator.ResolveContent(op.Ref, "ContentArea item"))))
                        {
                            continue;
                        }
                        added = writer.NewAreaItem(op.Ref, op.DisplayOption);
                    }
                    var at = op.At ?? items.Count;
                    CheckPosition(where, at, items.Count);
                    items.Insert(at, added);
                    break;

                case AreaOps.Remove:
                    items.RemoveAt(Locate(where, items, op));
                    break;

                case AreaOps.Set:
                    if (op.Index is not { } position || op.Ref is not null)
                    {
                        throw AgentException.Usage($"{where}: give the inline block's 'index' (an inline block has no ref).");
                    }
                    if (op.Values is null && op.Name is null)
                    {
                        throw AgentException.Usage($"{where}: give values or a name to set.");
                    }
                    writer.ChangeInlineItem(area, name, position, op.Values, op.Name);
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
                    throw AgentException.Usage($"areaOps[{i}]: unknown op '{op.Op}'.", $"Use {AreaOps.Add}, {AreaOps.Remove}, {AreaOps.Move} or {AreaOps.Set}.");
            }

            property.Value = area;
            commit();
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
        var found = items.ToList().FindIndex(item => Compat.InlineBlocks.Of(item) is null && item.ContentLink is { } link && link.CompareToIgnoreWorkID(target));
        if (found >= 0)
        {
            return found;
        }
        var shown = items.Select((item, i) => $"{i}: {(Compat.InlineBlocks.Of(item) is { } inline ? $"inline {PropertyValues.InlineTypeName(inline)}" : ContentReference.IsNullOrEmpty(item.ContentLink) ? "(none)" : item.ContentLink.ToReferenceWithoutVersion().ToString())}");
        throw AgentException.NotFound($"{where}: no item references {op.Ref}.",
            $"Items: {string.Join(", ", shown)}. An inline block has no ref: name it by its position.");
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
