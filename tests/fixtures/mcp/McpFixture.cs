// Copied into a copy of the edge-case site by setup.sh, for the MCP module's end-to-end tests
// (tests/OptiCli.Mcp.Integration). It adds a content type only administrators may create, a block type with properties
// the edit UI hides or locks, and at startup makes the test users, the access rights, a language only administrators may
// edit and the folders the tests rely on, on this site's database only. Passwords are generated, kept in
// App_Data/mcp-test-users.json (readable by the owner only) and never printed.
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EPiServer.Cms.UI.AspNetIdentity;
using EPiServer.DataAccess;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using EPiServer.Shell.ObjectEditing;
using EPiServer.Shell.ObjectEditing.EditorDescriptors;
using EPiServer.SpecializedProperties;
using Microsoft.AspNetCore.Identity;
using OptiCli.Mcp;

namespace OptiCliMcpFixture;

/// <summary>
/// A block type only WebAdmins may create (the type's access rights, as admin mode sets them): an editor's assistant
/// must not create it either, though the CMS doesn't check this on save.
/// </summary>
[ContentType(GUID = "5d6c1a2b-8e4f-4a7b-9c3d-2e1f0a9b8c71", DisplayName = "Restricted block", Description = "opticli MCP fixture: only WebAdmins may create it")]
[Access(Roles = "WebAdmins", Access = AccessLevel.Create)]
public class RestrictedBlock : BlockData
{
    public virtual string Heading { get; set; }
}

/// <summary>
/// A block type for what the edit UI decides about properties, and for rich text and links: a property it doesn't show,
/// one it shows locked, one an editor descriptor locks for everyone but administrators (only the CMS UI's own metadata
/// knows that), and one on a tab only administrators see. An editor's assistant may neither read nor write those.
/// </summary>
[ContentType(GUID = "8b1f4c2e-6d3a-4e5b-9f7c-1a2b3c4d5e6f", DisplayName = "MCP fields block", Description = "opticli MCP fixture: properties the edit UI hides or locks")]
public class McpFieldsBlock : BlockData
{
    public virtual string Heading { get; set; }

    [ScaffoldColumn(false)]
    public virtual string Hidden { get; set; }

    [Editable(false)]
    public virtual string Locked { get; set; }

    [UIHint(McpAdminOnlyEditorDescriptor.Hint)]
    public virtual string AdminScripts { get; set; }

    [Display(GroupName = McpFixtureTabs.AdminOnly)]
    public virtual string AdminTab { get; set; }

    public virtual XhtmlString Body { get; set; }

    public virtual LinkItemCollection Links { get; set; }

    public virtual Url Address { get; set; }

    public virtual ContentArea Area { get; set; }

    public virtual IList<ContentReference> Related { get; set; }

    public virtual ContentReference Target { get; set; }

    /// <summary>A local block, whose properties the edit UI's metadata nests in this block's.</summary>
    public virtual McpInnerBlock Inner { get; set; }

    /// <summary>A block list.</summary>
    public virtual IList<McpInnerBlock> Items { get; set; }
}

/// <summary>The local block and block list item of <see cref="McpFieldsBlock"/>, with a property locked for all but WebAdmins.</summary>
[ContentType(GUID = "9c2a5d3e-7f4b-4c6d-8e9f-0a1b2c3d4e5f", DisplayName = "MCP inner block", AvailableInEditMode = false)]
public class McpInnerBlock : BlockData
{
    public virtual string Title { get; set; }

    [UIHint(McpAdminOnlyEditorDescriptor.Hint)]
    public virtual string AdminOnly { get; set; }
}

/// <summary>Locks a property for everyone but WebAdmins, as a site's editor descriptor or metadata extender may.</summary>
[EditorDescriptorRegistration(TargetType = typeof(string), UIHint = Hint)]
public class McpAdminOnlyEditorDescriptor : EditorDescriptor
{
    public const string Hint = "McpAdminOnly";

    public override void ModifyMetadata(ExtendedMetadata metadata, IEnumerable<Attribute> attributes)
    {
        base.ModifyMetadata(metadata, attributes);
        if (!PrincipalInfo.CurrentPrincipal.IsInRole("WebAdmins"))
        {
            metadata.IsReadOnly = true;
        }
    }
}

/// <summary>A tab whose properties the edit UI shows only to those with Administer on the content.</summary>
[GroupDefinitions]
public static class McpFixtureTabs
{
    [RequiredAccess(AccessLevel.Administer)]
    [Display(Name = "MCP admin only", Order = 900)]
    public const string AdminOnly = "McpAdminOnly";
}

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

    /// <summary>The language only administrators may edit (admin mode, Languages); enabled by the fixture.</summary>
    public const string AdminLanguage = "de";

    /// <summary>
    /// A folder in the global assets with two folders below it, "find-fixture 1 shown", which editors see, and
    /// "find-fixture 2 hidden", which only administrators see: a search must not tell editors there is more.
    /// </summary>
    public static readonly Guid FindFolder = Guid.Parse("c3d4e5f6-a7b8-4c9d-8e0f-1a2b3c4d5e6f");

    /// <summary>
    /// A block with rich text and links an editor's assistant may not add, but which the CMS edit UI's own editor could
    /// have saved: written back as they are, with a typo fixed, they must be accepted.
    /// </summary>
    public static readonly Guid WriteBackBlock = Guid.Parse("d4e5f6a7-b8c9-4d0e-9f1a-2b3c4d5e6f70");

    public const string WriteBackBody = "<p>Watch teh video:</p><iframe src=\"https://www.youtube.com/embed/opticli\" width=\"560\" height=\"315\"></iframe>";

    /// <summary>
    /// A block whose rich text has script inside an element a browser reads as text: inert there, so it isn't one an
    /// editor's assistant may write back live.
    /// </summary>
    public static readonly Guid InertBlock = Guid.Parse("e5f6a7b8-c9d0-4e1f-8a2b-3c4d5e6f7081");

    public const string InertBody = "<p>Example:</p><textarea><img src=x onerror=alert(document.domain)></textarea>";

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
            // Enabled, and with exactly these roles, so a test that disabled the account or took a role away and failed
            // to undo it is repaired on restart.
            if (!user.IsApproved)
            {
                user.IsApproved = true;
                Check(Wait(users.UpdateAsync(user)));
            }
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

        // A language only administrators may edit.
        var languages = services.GetRequiredService<ILanguageBranchRepository>();
        var adminLanguage = languages.Load(CultureInfo.GetCultureInfo(AdminLanguage)) ?? new LanguageBranch(AdminLanguage);
        var editors = new[] { "WebAdmins", "Administrators" };
        if (!adminLanguage.Enabled || adminLanguage.ACL is null || adminLanguage.ACL.Count != editors.Length
            || !editors.All(role => adminLanguage.ACL.Any(e => e.Value.Name == role && e.Value.Access == AccessLevel.Edit)))
        {
            var writable = adminLanguage.CreateWritableClone();
            writable.Enabled = true;
            var acl = new AccessControlList();
            foreach (var role in editors)
            {
                acl.AddEntry(new AccessControlEntry(role, AccessLevel.Edit, SecurityEntityType.Role));
            }
            writable.ACL = acl;
            languages.Save(writable);
            Console.Error.WriteLine($"[mcp-fixture] language {AdminLanguage} enabled, for administrators only");
        }

        // The search fixture: one folder editors see, one they don't.
        var repository = services.GetRequiredService<IContentRepository>();
        var findFolder = Folder(repository, ContentReference.GlobalBlockFolder, "opticli find fixture", FindFolder);
        Folder(repository, findFolder, "find-fixture 1 shown", null);
        var hiddenFolder = Folder(repository, findFolder, "find-fixture 2 hidden", null);
        Ensure(security, findFolder, inherit: false,
            ("Administrators", AccessLevel.FullAccess),
            ("WebAdmins", AccessLevel.FullAccess),
            ("WebEditors", AccessLevel.Read | AccessLevel.Create | AccessLevel.Edit | AccessLevel.Delete | AccessLevel.Publish),
            ("ProductEditors", AccessLevel.Read));
        Ensure(security, hiddenFolder, inherit: false,
            ("Administrators", AccessLevel.FullAccess),
            ("WebAdmins", AccessLevel.FullAccess));

        // The write-back fixture, as the edit UI could have saved it.
        if (!repository.TryGet<IContent>(WriteBackBlock, out _))
        {
            var block = repository.GetDefault<McpFieldsBlock>(ContentReference.GlobalBlockFolder);
            var content = (IContent)block;
            content.Name = "opticli write-back fixture";
            content.ContentGuid = WriteBackBlock;
            block.Heading = "Write-back";
            block.Body = new XhtmlString(WriteBackBody);
            block.Links = new LinkItemCollection { new LinkItem { Href = "sms:+4712345678", Text = "Text us" } };
            repository.Save(content, SaveAction.Publish, AccessLevel.NoAccess);
            Console.Error.WriteLine("[mcp-fixture] created the write-back fixture");
        }
        if (!repository.TryGet<IContent>(InertBlock, out _))
        {
            var block = repository.GetDefault<McpFieldsBlock>(ContentReference.GlobalBlockFolder);
            var content = (IContent)block;
            content.Name = "opticli inert fixture";
            content.ContentGuid = InertBlock;
            block.Body = new XhtmlString(InertBody);
            repository.Save(content, SaveAction.Publish, AccessLevel.NoAccess);
            Console.Error.WriteLine("[mcp-fixture] created the inert fixture");
        }
    }

    /// <summary>The folder of that name below <paramref name="parent"/>, made if it isn't there.</summary>
    private static ContentReference Folder(IContentRepository repository, ContentReference parent, string name, Guid? guid)
    {
        if (guid is { } known && repository.TryGet<ContentFolder>(known, out var byGuid))
        {
            return byGuid.ContentLink;
        }
        if (repository.GetChildren<ContentFolder>(parent).FirstOrDefault(f => f.Name == name) is { } existing)
        {
            return existing.ContentLink;
        }
        var folder = repository.GetDefault<ContentFolder>(parent);
        folder.Name = name;
        if (guid is { } fixedGuid)
        {
            folder.ContentGuid = fixedGuid;
        }
        Console.Error.WriteLine($"[mcp-fixture] created the folder {name}");
        return repository.Save(folder, SaveAction.Publish, AccessLevel.NoAccess).ToReferenceWithoutVersion();
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

/// <summary>
/// The site's own API controller, with a named route that makes a link to itself: on a site whose controllers are
/// mapped twice ("Duplicate endpoint name", what mapping the module before <c>MapContent()</c> causes with a
/// <c>MapControllers()</c> after it and <see cref="McpFixtureAddOnRoutes"/>), every request fails. Startup maps the
/// controllers after <c>MapContent()</c>, as many sites do.
/// </summary>
[Microsoft.AspNetCore.Mvc.ApiController]
public class McpFixturePingController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [Microsoft.AspNetCore.Mvc.HttpGet("api/mcp-fixture/ping", Name = "McpFixturePing")]
    public Microsoft.AspNetCore.Mvc.IActionResult Ping() => Ok(new { self = Url.Link("McpFixturePing", null) });
}

/// <summary>
/// The site's custom error page, for <c>UseStatusCodePagesWithReExecute("/error/{0}")</c> in Startup: GET only, as an
/// error controller usually is, so an error without a body re-executed for a POST becomes a 405. The module's own
/// errors must reach the client as the module wrote them.
/// </summary>
public class McpFixtureErrorController : Microsoft.AspNetCore.Mvc.Controller
{
    [Microsoft.AspNetCore.Mvc.HttpGet("error/{code:int}")]
    public Microsoft.AspNetCore.Mvc.IActionResult Error(int code)
    {
        Response.StatusCode = code;
        return Content($"<!doctype html><title>Error {code}</title><h1>Site error page {code}</h1>", "text/html");
    }
}

/// <summary>
/// Routes an add-on registers through the CMS's extension point, which <c>MapContent()</c> maps, as many add-ons do:
/// what the CMS freezes when another endpoint was mapped before <c>MapContent()</c>.
/// </summary>
[ServiceConfiguration(typeof(EPiServer.Web.Routing.IEndpointRoutingExtension))]
public class McpFixtureAddOnRoutes : EPiServer.Web.Routing.IEndpointRoutingExtension
{
    public void MapEndpoints(IEndpointRouteBuilder endpointRouteBuilder) =>
        endpointRouteBuilder.MapControllerRoute("McpFixtureAddOn", "mcp-fixture-addon/{action=Index}", new { controller = "McpFixtureAddOn" });
}
