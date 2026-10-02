// Copied into a copy of the edge-case site by setup.sh, for the MCP module's end-to-end tests
// (tests/OptiCli.Mcp.Integration). At startup it makes the test users and the access rights the tests rely on, on
// this site's database only. Passwords are generated, kept in App_Data/mcp-test-users.json (readable by the owner
// only) and never printed.
using System.Security.Cryptography;
using System.Text.Json;
using EPiServer.Cms.UI.AspNetIdentity;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using Microsoft.AspNetCore.Identity;
using OptiCli.Mcp;

namespace OptiCliMcpFixture;

[InitializableModule]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public class McpFixture : IInitializableModule
{
    /// <summary>Where the passwords go, below the site's content root: the tests read it (OPTICLI_MCP_IT_USERS).</summary>
    public const string UsersFile = "App_Data/mcp-test-users.json";

    /// <summary>
    /// The test users and their exact roles: an administrator, an editor with full editing rights, a product editor who
    /// may edit but not publish the hidden page, and a user with no role at all, whom the role gate turns away.
    /// </summary>
    public static readonly (string User, string[] Roles)[] Users =
    [
        ("mcp-admin", ["WebAdmins", "WebEditors"]),
        ("mcp-editor", ["WebEditors"]),
        ("mcp-product", ["ProductEditors"]),
        ("mcp-visitor", []),
    ];

    /// <summary>Alloy's "Alloy Meet" product page: the page only administrators and product editors can see.</summary>
    public static readonly Guid HiddenPage = Guid.Parse("456929c5-d6b8-46c5-b339-896be5ccfddc");

    /// <summary>
    /// Settings from the <c>OptiCli:Mcp</c> configuration section beat the ones Startup passes to AddOptiCliMcp, so the
    /// tests can start this site in another configuration (<c>OptiCli__Mcp__AllowPublish=false</c>). The module itself
    /// lets Startup's delegate win, as a site would want.
    /// </summary>
    public static void ApplyConfiguration(OptiCliMcpOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(OptiCliMcpOptions.SectionName);
        if (section[nameof(OptiCliMcpOptions.AllowPublish)] is { Length: > 0 } publish)
        {
            options.AllowPublish = bool.Parse(publish);
        }
        if (section[nameof(OptiCliMcpOptions.AllowDelete)] is { Length: > 0 } delete)
        {
            options.AllowDelete = bool.Parse(delete);
        }
    }

    public void Initialize(InitializationEngine context) => context.InitComplete += (_, _) =>
    {
        try
        {
            Setup(context.Locate.Advanced);
        }
        catch (Exception e)
        {
            // The site should still start; setup.sh looks for this line in the log.
            Console.Error.WriteLine($"[mcp-fixture] setup failed: {e}");
        }
    };

    public void Uninitialize(InitializationEngine context)
    {
    }

    private static void Setup(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var file = Path.Combine(env.ContentRootPath, UsersFile);
        var passwords = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [] : [];

        foreach (var role in Users.SelectMany(u => u.Roles).Distinct())
        {
            if (!Wait(roles.RoleExistsAsync(role)))
            {
                Check(Wait(roles.CreateAsync(new IdentityRole(role))));
            }
        }
        foreach (var (name, userRoles) in Users)
        {
            var user = Wait(users.FindByNameAsync(name));
            if (user is null)
            {
                var password = NewPassword();
                user = new ApplicationUser { UserName = name, Email = $"{name}@example.test", EmailConfirmed = true, IsApproved = true };
                Check(Wait(users.CreateAsync(user, password)));
                passwords[name] = password;
                Console.Error.WriteLine($"[mcp-fixture] created {name}");
            }
            else if (!passwords.TryGetValue(name, out var known) || !Wait(users.CheckPasswordAsync(user, known)))
            {
                // A database copied again, or a lost file: the password is set anew rather than guessed.
                var password = NewPassword();
                Check(Wait(users.RemovePasswordAsync(user)));
                Check(Wait(users.AddPasswordAsync(user, password)));
                passwords[name] = password;
                Console.Error.WriteLine($"[mcp-fixture] reset the password of {name}");
            }
            // Exactly these roles, so a test that took a role away and failed to give it back is repaired on restart.
            var current = Wait(users.GetRolesAsync(user));
            foreach (var extra in current.Except(userRoles, StringComparer.OrdinalIgnoreCase))
            {
                Check(Wait(users.RemoveFromRoleAsync(user, extra)));
            }
            foreach (var missing in userRoles.Except(current, StringComparer.OrdinalIgnoreCase))
            {
                Check(Wait(users.AddToRoleAsync(user, missing)));
            }
        }
        WritePrivately(file, JsonSerializer.Serialize(passwords, new JsonSerializerOptions { WriteIndented = true }));

        var security = services.GetRequiredService<IContentSecurityRepository>();
        // The root: administrators everything, editors everything but Administer. The entries already there stay,
        // Everyone's Read among them.
        Ensure(security, ContentReference.RootPage, inherit: true,
            ("WebAdmins", AccessLevel.FullAccess),
            ("WebEditors", AccessLevel.Read | AccessLevel.Create | AccessLevel.Edit | AccessLevel.Delete | AccessLevel.Publish));
        // The hidden page: only administrators and product editors see it, and product editors may edit but not
        // publish it.
        if (services.GetRequiredService<IContentLoader>().TryGet<IContent>(HiddenPage, out var hidden) && !hidden.IsDeleted)
        {
            Ensure(security, hidden.ContentLink.ToReferenceWithoutVersion(), inherit: false,
                ("Administrators", AccessLevel.FullAccess),
                ("WebAdmins", AccessLevel.FullAccess),
                ("ProductEditors", AccessLevel.Read | AccessLevel.Edit));
        }
        else
        {
            Console.Error.WriteLine($"[mcp-fixture] setup failed: the hidden page {HiddenPage} (Alloy Meet) isn't there.");
        }
    }

    /// <summary>
    /// Adds the entries; with <paramref name="inherit"/> false, replaces the content's access rights with exactly these
    /// (no longer inherited). Saves only when something differs, so a restart changes nothing.
    /// </summary>
    private static void Ensure(IContentSecurityRepository security, ContentReference link, bool inherit, params (string Role, AccessLevel Level)[] entries)
    {
        var current = security.Get(link);
        var present = entries.All(e => current.Entries.Any(x => x.Name == e.Role && x.EntityType == SecurityEntityType.Role && x.Access == e.Level));
        var exact = inherit || current.Entries.Count() == entries.Length;
        if (present && exact && current.IsInherited == inherit)
        {
            return;
        }
        var writable = (IContentSecurityDescriptor)current.CreateWritableClone();
        if (!inherit)
        {
            writable.ToLocal();
            writable.Clear();
        }
        foreach (var (role, level) in entries)
        {
            writable.RemoveEntry(new AccessControlEntry(role, AccessLevel.NoAccess, SecurityEntityType.Role));
            writable.AddEntry(new AccessControlEntry(role, level, SecurityEntityType.Role));
        }
        security.Save(link, writable, SecuritySaveType.Replace);
        Console.Error.WriteLine($"[mcp-fixture] access rights set on {link}");
    }

    /// <summary>Meets ASP.NET Identity's default rules: upper and lower case, a digit and a symbol, 26 characters.</summary>
    private static string NewPassword() => "Mcp-1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(9)) + "!a";

    private static void WritePrivately(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
