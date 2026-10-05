using OptiCli.Mcp.Tests.OAuth;

namespace EPiServer.Cms.UI.AspNetIdentity;

/// <summary>
/// A stand-in with the full name of ASP.NET Identity's user provider, which the module knows by name (it doesn't
/// reference EPiServer.CMS.UI.AspNetIdentity): under a name of its own, so only its type tells.
/// </summary>
internal class ApplicationUserProvider<TUser>() : FakeUserProvider("Stand-in");
