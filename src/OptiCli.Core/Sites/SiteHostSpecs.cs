using System.Text.Json.Nodes;
using OptiCli.Protocol;

namespace OptiCli.Core.Sites;

/// <summary>
/// The hosts of a new site as <c>sites add --host</c> and a plan's <c>site</c> step give them: <c>name[:port][@lang]</c>,
/// with an optional scheme (<c>https://alloy.localhost:5001</c>), or in a plan also an object
/// (<c>{"name": "alloy.localhost:5001", "type": "edit", "lang": "sv", "https": true}</c>).
/// </summary>
public static class SiteHostSpecs
{
    public const string Syntax = "name[:port][@lang], optionally with a scheme (https://alloy.localhost:5001@sv), or * for host names no other site has";

    /// <summary><c>host@lang</c> as host and language; a host name has no <c>@</c>, so the last one splits them.</summary>
    public static SiteAddHost Parse(string text)
    {
        var trimmed = text.Trim();
        var at = trimmed.LastIndexOf('@');
        return at > 0 && at < trimmed.Length - 1
            ? new SiteAddHost(trimmed[..at], Language: trimmed[(at + 1)..])
            : new SiteAddHost(trimmed);
    }

    /// <summary>A plan's <c>hosts</c>: an array of strings (<see cref="Parse"/>) or objects with <c>name</c>, <c>type</c>, <c>lang</c> and <c>https</c>.</summary>
    /// <param name="problems">Gets one entry per host that isn't one of those.</param>
    public static IReadOnlyList<SiteAddHost> FromJson(JsonNode? node, string where, List<string> problems)
    {
        if (node is JsonValue single && single.TryGetValue<string>(out var one))
        {
            return [Parse(one)];
        }
        if (node is not JsonArray array || array.Count == 0)
        {
            problems.Add($"{where}: \"hosts\" must be a non-empty array of hosts ({Syntax}), or objects {{\"name\", \"type\", \"lang\", \"https\"}}.");
            return [];
        }
        var hosts = new List<SiteAddHost>();
        for (var i = 0; i < array.Count; i++)
        {
            switch (array[i])
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    hosts.Add(Parse(text));
                    break;
                case JsonObject obj when obj["name"] is JsonValue name && name.TryGetValue<string>(out var hostName):
                    foreach (var key in obj.Select(p => p.Key).Where(k => k is not ("name" or "type" or "lang" or "https")))
                    {
                        problems.Add($"{where}: \"hosts\"[{i}] has unknown field \"{key}\" (allowed: name, type, lang, https).");
                    }
                    hosts.Add(new SiteAddHost(hostName, Text(obj, "type", i, where, problems), Text(obj, "lang", i, where, problems), Flag(obj, "https", i, where, problems)));
                    break;
                default:
                    problems.Add($"{where}: \"hosts\"[{i}] must be a string ({Syntax}) or an object with \"name\".");
                    break;
            }
        }
        return hosts;
    }

    private static string? Text(JsonObject obj, string key, int index, string where, List<string> problems)
    {
        if (obj[key] is null)
        {
            return null;
        }
        if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }
        problems.Add($"{where}: \"hosts\"[{index}].{key} must be a string.");
        return null;
    }

    private static bool? Flag(JsonObject obj, string key, int index, string where, List<string> problems)
    {
        if (obj[key] is null)
        {
            return null;
        }
        if (obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }
        problems.Add($"{where}: \"hosts\"[{index}].{key} must be true or false.");
        return null;
    }
}
