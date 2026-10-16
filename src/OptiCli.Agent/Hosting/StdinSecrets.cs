using System.Text.Json;
using OptiCli.Protocol;

namespace OptiCli.Agent.Hosting;

/// <summary>
/// The sealed apply's way of giving the site its token and connection string: one line on stdin
/// (<see cref="AgentProtocol.SecretsVariable"/>), set as environment variables inside the process only, so they never
/// show in its environment block, and removed again once the site has started (<see cref="Forget"/>).
/// </summary>
internal static class StdinSecrets
{
    /// <summary>True when the CLI started the site with its secrets on stdin.</summary>
    public static bool Expected =>
        string.Equals(Environment.GetEnvironmentVariable(AgentProtocol.SecretsVariable)?.Trim(), AgentProtocol.SecretsFromStdin, StringComparison.Ordinal);

    /// <summary>The names <see cref="Read"/> set, for <see cref="Forget"/>.</summary>
    private static IReadOnlyList<string> _set = [];

    /// <summary>Reads the line and sets its variables in this process.</summary>
    /// <exception cref="InvalidOperationException">No line, or not the expected shape: the site must not start without them.</exception>
    public static void Read(TextReader input)
    {
        var variables = Parse(input.ReadLine());
        foreach (var (name, value) in variables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        _set = variables.Select(v => v.Key).ToList();
    }

    /// <exception cref="InvalidOperationException">Not a JSON object of strings with the token, the pinned connection and only names the protocol allows.</exception>
    internal static IReadOnlyList<KeyValuePair<string, string>> Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException($"[opticli] Refusing to start: {AgentProtocol.SecretsVariable}={AgentProtocol.SecretsFromStdin}, but stdin had no line with the run's secrets.");
        }
        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(line);
        }
        catch (JsonException)
        {
            parsed = null;
        }
        if (parsed is null || parsed.Keys.Any(name => !AgentProtocol.IsSecret(name))
            || !parsed.ContainsKey(AgentProtocol.TokenVariable) || !parsed.ContainsKey(AgentProtocol.DatabaseVariable))
        {
            // Never echo the line: it holds the credentials.
            throw new InvalidOperationException("[opticli] Refusing to start: the run's secrets on stdin aren't what opticli sends (the token, the pinned connection string, connection strings only).");
        }
        return parsed.ToList();
    }

    /// <summary>Removes the variables again, so processes the site starts from now on don't inherit them.</summary>
    public static void Forget()
    {
        foreach (var name in _set)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
