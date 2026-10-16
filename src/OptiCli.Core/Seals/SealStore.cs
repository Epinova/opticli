using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Seals;

/// <summary>
/// Where seals are kept: <c>seals/&lt;id&gt;.json</c> in the user config folder (next to <c>config.json</c>), each with an
/// HMAC-SHA256 over its content, keyed with <c>seal.key</c> there (32 random bytes, user-only, made on first use). A seal
/// that was edited, or written by hand, fails the MAC. Seals are used once: a run creates <c>seals/&lt;id&gt;.used</c>
/// exclusively before it writes anything.
/// </summary>
/// <remarks>
/// The key is readable by anything running as the user, so the MAC makes tampering detectable and deliberate rather than
/// impossible; the run trusts a seal only for what it can't see again (that the local dry run passed, and what
/// production looked like then), and checks everything else itself.
/// </remarks>
public sealed partial class SealStore(string folder, string keyFile)
{
    /// <summary><c>details.reason</c> for a seal that fails its MAC or can't be read.</summary>
    public const string TamperedReason = "sealTampered";

    /// <summary><c>details.reason</c> for a seal that was run before.</summary>
    public const string UsedReason = "sealUsed";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Folder { get; } = folder;

    public static SealStore For(OptiCliEnvironment environment)
    {
        var config = Path.GetDirectoryName(environment.UserConfigFile)!;
        return new SealStore(Path.Combine(config, "seals"), Path.Combine(config, "seal.key"));
    }

    /// <summary>A new seal id: 16 hex digits (64 random bits).</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>Only ids of this shape name a file: <c>--seal ../x</c> can't.</summary>
    public static bool IsId(string? id) => id is not null && IdShape().IsMatch(id);

    public string PathOf(string id) => Path.Combine(Folder, $"{id}.json");

    private string UsedPath(string id) => Path.Combine(Folder, $"{id}.used");

    /// <returns>The seal's file.</returns>
    public string Save(Seal seal)
    {
        CreateFolder();
        var body = JsonSerializer.Serialize(seal, Json);
        var path = PathOf(seal.Id);
        // The MAC is over the seal's JSON exactly as written, which reading takes back verbatim (GetRawText).
        UserConfig.Replace(path, $"{{\n\"mac\": \"{Mac(body)}\",\n\"seal\": {body}\n}}\n");
        return path;
    }

    /// <exception cref="UsageException">Not a seal id.</exception>
    /// <exception cref="NotFoundException">No such seal.</exception>
    /// <exception cref="RefusedException">It fails its MAC or can't be read (<see cref="TamperedReason"/>), or was used (<see cref="UsedReason"/>).</exception>
    public Seal Load(string id)
    {
        if (!IsId(id))
        {
            throw new UsageException($"'{id}' is not a seal id (16 hex digits, as `opticli apply --dry-run --seal-for` printed it).");
        }
        var path = PathOf(id);
        if (!File.Exists(path))
        {
            throw new NotFoundException($"There is no seal {id} ({path}).", "Seal the plan again (`opticli apply <plan> --dry-run --seal-for <name>`), then run the command it prints.");
        }
        if (File.Exists(UsedPath(id)))
        {
            throw Refused($"Seal {id} was run before; a seal is used once.", UsedReason, id);
        }
        Seal? seal;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var raw = document.RootElement.GetProperty("seal").GetRawText();
            var mac = document.RootElement.GetProperty("mac").GetString() ?? "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(Mac(raw, create: false) ?? "")))
            {
                throw Refused($"Seal {id} doesn't match its MAC: it was changed after it was made, or not made by opticli.", TamperedReason, id);
            }
            seal = JsonSerializer.Deserialize<Seal>(raw, Json);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            throw Refused($"Seal {id} can't be read as a seal: {ex.Message}", TamperedReason, id);
        }
        return seal is not null && seal.Id == id ? seal : throw Refused($"Seal {id} is not the seal its file name says.", TamperedReason, id);
    }

    /// <summary>Marks the seal used, once: a second run (or two at once) is refused.</summary>
    /// <exception cref="RefusedException">It was used already.</exception>
    public void Consume(string id)
    {
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using var stream = new FileStream(UsedPath(id), options);
            stream.Write(Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O") + "\n"));
        }
        catch (IOException) when (File.Exists(UsedPath(id)))
        {
            throw Refused($"Seal {id} was run before; a seal is used once.", UsedReason, id);
        }
    }

    private void CreateFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Folder);
        }
        else
        {
            Directory.CreateDirectory(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <param name="create">Make the key when there is none (sealing); reading without a key fails the MAC.</param>
    private string? Mac(string body, bool create = true) =>
        Key(create) is { } key ? Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body))).ToLowerInvariant() : null;

    private byte[]? Key(bool create)
    {
        if (File.Exists(keyFile))
        {
            return ReadKey();
        }
        if (!create)
        {
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using var stream = new FileStream(keyFile, options);
            stream.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n"));
        }
        catch (IOException) when (File.Exists(keyFile))
        {
            // Another opticli made it at the same moment: use that one.
        }
        return ReadKey();
    }

    private byte[] ReadKey()
    {
        try
        {
            var key = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
            return key.Length >= 32 ? key : throw new FormatException("too short");
        }
        catch (FormatException ex)
        {
            throw new RefusedException($"The seal key {keyFile} is broken ({ex.Message}).",
                "Deleting it makes a new key on the next seal; every seal made before then no longer matches (seal those plans again).")
            {
                Details = new { reason = TamperedReason },
            };
        }
    }

    private static RefusedException Refused(string message, string reason, string id) =>
        new(message, reason == UsedReason
            ? "Seal the plan again (`opticli apply <plan> --dry-run --seal-for <name>`): production may have changed since that run."
            : "Seal the plan again with `opticli apply <plan> --dry-run --seal-for <name>`, and don't edit seal files.")
        {
            Details = new { reason, seal = id },
        };

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex IdShape();
}
