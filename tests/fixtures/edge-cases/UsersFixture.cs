// Copied into the edge-case site by setup.sh, for opticli's `users` integration tests: a user that opticli didn't make,
// which `opticli users remove` must refuse to remove.
using EPiServer.Cms.UI.AspNetIdentity;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
using Microsoft.AspNetCore.Identity;

namespace OptiCliEdgeCases;

/// <summary>
/// At startup, makes <see cref="Name"/> through ASP.NET Identity if it doesn't exist: in WebEditors, without a password
/// (nobody signs in as it) and without opticli's tag.
/// </summary>
[InitializableModule]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public class OptiCliUsersFixture : IInitializableModule
{
    public const string Name = "edge-fixture-user";

    public void Initialize(InitializationEngine context)
    {
        try
        {
            using var scope = context.Locate.Advanced.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (users.FindByNameAsync(Name).GetAwaiter().GetResult() is not null)
            {
                return;
            }
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!roles.RoleExistsAsync("WebEditors").GetAwaiter().GetResult())
            {
                roles.CreateAsync(new IdentityRole("WebEditors")).GetAwaiter().GetResult();
            }
            var user = new ApplicationUser { UserName = Name, Email = $"{Name}@example.com", IsApproved = true };
            users.CreateAsync(user).GetAwaiter().GetResult();
            users.AddToRoleAsync(user, "WebEditors").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[edge-cases] setup failed: {ex}");
        }
    }

    public void Uninitialize(InitializationEngine context)
    {
    }
}
