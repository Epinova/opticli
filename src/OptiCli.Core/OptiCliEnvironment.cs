using System.Collections;

namespace OptiCli.Core;

/// <summary>
/// The parts of the process environment discovery depends on, captured once so tests can supply
/// a fake home directory, working directory and environment variables.
/// </summary>
public sealed class OptiCliEnvironment
{
    private readonly IReadOnlyDictionary<string, string> _variables;

    public OptiCliEnvironment(string currentDirectory, IReadOnlyDictionary<string, string> variables)
    {
        CurrentDirectory = Path.GetFullPath(currentDirectory);
        // Windows environment variable names are case-insensitive ($env:opticli_db is OPTICLI_DB).
        _variables = new Dictionary<string, string>(variables, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        HomeDirectory = Variable("HOME") ?? Variable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string CurrentDirectory { get; }

    public string HomeDirectory { get; }

    public static OptiCliEnvironment FromProcess()
    {
        var variables = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                variables[key] = value;
            }
        }
        return new OptiCliEnvironment(Environment.CurrentDirectory, variables);
    }

    public string? Variable(string name) =>
        _variables.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>
    /// Same rule as Microsoft.Extensions.Configuration.UserSecrets: <c>%APPDATA%\Microsoft\UserSecrets</c>
    /// when APPDATA is set (Windows), otherwise <c>~/.microsoft/usersecrets</c>.
    /// </summary>
    public string UserSecretsFile(string userSecretsId) => Variable("APPDATA") is { } appData
        ? Path.Combine(appData, "Microsoft", "UserSecrets", userSecretsId, "secrets.json")
        : Path.Combine(HomeDirectory, ".microsoft", "usersecrets", userSecretsId, "secrets.json");

    /// <summary><c>%APPDATA%\opticli\config.json</c> on Windows, else <c>$XDG_CONFIG_HOME/opticli/config.json</c> (default <c>~/.config</c>).</summary>
    public string UserConfigFile => Variable("APPDATA") is { } appData
        ? Path.Combine(appData, "opticli", "config.json")
        : Path.Combine(Variable("XDG_CONFIG_HOME") ?? Path.Combine(HomeDirectory, ".config"), "opticli", "config.json");

    /// <summary>
    /// Where <c>serve</c> keeps its per-project state and log files: <c>%LOCALAPPDATA%\opticli</c> on Windows,
    /// else <c>$XDG_STATE_HOME/opticli</c> (default <c>~/.local/state/opticli</c>).
    /// </summary>
    public string StateDirectory => Variable("LOCALAPPDATA") is { } localAppData
        ? Path.Combine(localAppData, "opticli")
        : Path.Combine(Variable("XDG_STATE_HOME") ?? Path.Combine(HomeDirectory, ".local", "state"), "opticli");
}
