using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EPiServer.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OptiCli.Agent.Http;
using OptiCli.Agent.Safety;
using OptiCli.Protocol;

namespace OptiCli.Agent.Hosting;

/// <summary>
/// IDE mode (<c>opticli ide on</c>): the developer starts the site from the IDE, with the hook from the build's
/// runtimeconfig and no variables from the CLI. The agent makes its own token and, once the site listens, writes an
/// <see cref="IdeAnnouncement"/> for the CLI to find. Unlike a run of <c>serve</c>, nothing here stops the site from
/// starting: when the agent can't be used (another database, not Development), it stays off for the run and says why.
/// </summary>
internal static class IdeRun
{
    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>This run's token: made once per process, so both reads of the settings (hook and hosting startup) share it.</summary>
    public static readonly string Token =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? _offReason = "The site hasn't finished starting yet.";

    /// <summary>Why the agent is off for this run; null once it has announced itself.</summary>
    public static string? OffReason => Volatile.Read(ref _offReason);

    /// <summary>This process is the site the developer runs from the IDE: what opticli changes shows there without a restart.</summary>
    public static bool Active { get; private set; }

    /// <summary>Announces the agent once the site listens, and withdraws it when the site stops.</summary>
    public static void Register(IApplicationBuilder app, AgentSettings settings)
    {
        Active = true;
        var services = app.ApplicationServices;
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() => Announce(services, settings));
        lifetime.ApplicationStopping.Register(() => Withdraw(settings.IdeFile!, settings.Token!));
    }

    private static void Announce(IServiceProvider services, AgentSettings settings)
    {
        try
        {
            var environment = services.GetRequiredService<IWebHostEnvironment>();
            var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
            var problem = Problem(environment.EnvironmentName, services.GetRequiredService<IOptions<DataAccessOptions>>().Value, settings, addresses, out var verdict);
            if (problem is not null)
            {
                Off(problem);
                return;
            }
            Write(settings.IdeFile!, new IdeAnnouncement(
                AgentProtocol.Version,
                AgentInfo.Version,
                Environment.ProcessId,
                ProcessStartTime(),
                settings.Token!,
                addresses,
                DateTimeOffset.UtcNow,
                verdict!.Server,
                verdict.Database));
            Volatile.Write(ref _offReason, null);
            Console.Error.WriteLine($"[opticli] IDE mode: agent {AgentInfo.Version} ready for opticli at {string.Join(", ", addresses)} (database '{verdict.Database}' on '{verdict.Server}')");
        }
        catch (Exception ex)
        {
            // Never the site's problem: it runs on without the agent.
            Off($"The agent couldn't announce itself: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Why the agent can't be used in this run; null when it can.</summary>
    /// <param name="verdict">The database the CMS connects to, checked.</param>
    internal static string? Problem(string environmentName, DataAccessOptions options, AgentSettings settings, IReadOnlyCollection<string> addresses, out GuardVerdict? verdict)
    {
        verdict = null;
        if (!string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            return $"The site runs as '{environmentName}'; the agent only runs in Development.";
        }
        foreach (var connection in options.ConnectionStrings)
        {
            var checkedOne = ConnectionGuard.Check(connection.ConnectionString);
            if (!settings.Allows(checkedOne))
            {
                return $"The CMS connection '{connection.Name}' is not local{(settings.ApprovedRemote is null ? "" : " nor the approved development database")}: {checkedOne.Reason}";
            }
        }
        var used = DatabasePin.Resolve(options);
        if (used is null)
        {
            return "The CMS has no connection string.";
        }
        verdict = ConnectionGuard.Check(used.ConnectionString);
        if (addresses.Count == 0)
        {
            return "The site reports no address it listens on (IIS?), so opticli can't reach it.";
        }
        return null;
    }

    private static void Off(string reason)
    {
        Volatile.Write(ref _offReason, reason);
        Console.Error.WriteLine($"[opticli] IDE mode: the agent is off for this run. {reason}");
    }

    private static DateTimeOffset ProcessStartTime()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }

    /// <summary>Writes atomically (temp file + rename), readable by the user only from the start.</summary>
    internal static void Write(string path, IdeAnnouncement announcement)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UserOnlyFile;
            }
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(announcement, AgentJson.Options));
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Removes the announcement while it is this run's: another run of the site may have replaced it.</summary>
    internal static void Withdraw(string path, string token)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<IdeAnnouncement>(File.ReadAllText(path), AgentJson.Options)?.Token == token)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The CLI removes an announcement whose process is gone.
        }
    }
}
