using System.Security.Claims;
using System.Security.Principal;
using EPiServer.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Protocol;

namespace OptiCli.Agent.Safety;

/// <summary>
/// Makes the CMS see every agent request as the <c>opticli</c> user, so saves are attributed to it.
/// </summary>
/// <remarks>
/// The CMS stamps a version's "saved by" (<c>tblWorkContent.ChangedByName</c>) with
/// <c>IPrincipalAccessor.Principal.Identity.Name</c> at save time; it ignores the ChangedBy value on
/// the content itself. Setting the principal through the accessor covers both the HttpContext user
/// (what the CMS's accessor reads) and its thread fallback. The admin roles only satisfy code that
/// checks roles; saves themselves pass <c>AccessLevel.NoAccess</c>.
/// </remarks>
internal sealed class OptiCliPrincipal : IDisposable
{
    private static readonly string[] Roles = ["WebAdmins", "Administrators"];

    private readonly HttpContext _context;
    private readonly IPrincipalAccessor _accessor;
    private readonly ClaimsPrincipal _previousUser;
    private readonly IPrincipal? _previousPrincipal;

    private OptiCliPrincipal(HttpContext context)
    {
        _context = context;
        _accessor = context.RequestServices.GetRequiredService<IPrincipalAccessor>();
        _previousUser = context.User;
        _previousPrincipal = _accessor.Principal;

        var principal = new GenericPrincipal(new GenericIdentity(AgentProtocol.PrincipalName, "opticli"), Roles);
        _accessor.Principal = principal;
        context.User = principal;
    }

    public static IDisposable Enter(HttpContext context) => new OptiCliPrincipal(context);

    public void Dispose()
    {
        if (_previousPrincipal is not null)
        {
            _accessor.Principal = _previousPrincipal;
        }
        _context.User = _previousUser;
    }
}
