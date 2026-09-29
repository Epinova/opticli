using System.Text.Json.Nodes;

namespace OptiCli.Core.Properties;

/// <summary>The output rule for long text: cut at <see cref="MaxLength"/> and say so, unless asked for all of it.</summary>
public static class TextValues
{
    public const int MaxLength = 300;

    /// <summary>Sets <c>value</c> on <paramref name="target"/>, plus <c>truncated</c>/<c>length</c> when cut.</summary>
    public static void Put(JsonObject target, string value, bool full)
    {
        if (full || value.Length <= MaxLength)
        {
            target["value"] = value;
            return;
        }
        target["value"] = Cut(value);
        target["truncated"] = true;
        target["length"] = value.Length;
    }

    /// <summary>Cuts long strings inside a JSON value; returns whether anything was cut.</summary>
    public static bool CutStrings(JsonNode? node)
    {
        var cut = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > MaxLength)
                    {
                        obj[key] = Cut(text) + "…";
                        cut = true;
                    }
                    else
                    {
                        cut |= CutStrings(obj[key]);
                    }
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > MaxLength)
                    {
                        array[i] = Cut(text) + "…";
                        cut = true;
                    }
                    else
                    {
                        cut |= CutStrings(array[i]);
                    }
                }
                break;
        }
        return cut;
    }

    private static string Cut(string value)
    {
        var end = MaxLength;
        if (char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }
        return value[..end];
    }
}
