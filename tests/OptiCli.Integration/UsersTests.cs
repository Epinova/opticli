using System.Net;
using System.Text.RegularExpressions;
using OptiCli.Core;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Users;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// <c>users add|remove|roles</c> on the edge-case site (ASP.NET Identity), through the running site: a user opticli
/// makes can sign in on the CMS's login page, and only such a user is removed. Each test removes the users it made.
/// </summary>
public sealed partial class UsersTests
{
    /// <summary>The user UsersFixture.cs makes at startup, which opticli didn't make.</summary>
    private const string FixtureUser = "edge-fixture-user";

    [SiteFact]
    public async Task A_user_opticli_adds_signs_in_and_is_removed_again()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var name = $"opticli-it-{Guid.NewGuid():N}"[..20];
        var password = PasswordFiles.Generate();
        try
        {
            var dry = await Add(site, name, password, dryRun: true, cancellationToken);
            Assert.Equal((false, true), (dry.Created, dry.DryRun));
            Assert.False(await SignInAsync(name, password, cancellationToken));

            var added = await Add(site, name, password, dryRun: false, cancellationToken);
            Assert.Equal((true, $"{name}@{LocalUsers.EmailDomain}", LocalUsers.DefaultRole), (added.Created, added.Email, Assert.Single(added.Roles)));
            Assert.True(await SignInAsync(name, password, cancellationToken));
            Assert.False(await SignInAsync(name, password + "x", cancellationToken));

            var again = await Assert.ThrowsAsync<ConflictException>(() => Add(site, name, PasswordFiles.Generate(), dryRun: false, cancellationToken));
            Assert.Contains("exists already", again.Message);

            var roles = await site.Agent.SendAsync<UserRolesResult>(HttpMethod.Get, AgentRoutes.UserRoles, null, cancellationToken);
            Assert.Contains(roles.Roles, r => r.Name == LocalUsers.DefaultRole && r.Members >= 1 && r.VirtualRoles.Contains("CmsAdmins"));
            Assert.True(roles.OptiCliUsers >= 1);
            Assert.DoesNotContain(name, System.Text.Json.JsonSerializer.Serialize(roles, AgentJson.Options));

            var removed = await site.Agent.SendAsync<UserRemoveResult>(HttpMethod.Post, AgentRoutes.UserRemove, new UserRemoveRequest { Name = name }, cancellationToken);
            Assert.True(removed.Removed);
            Assert.False(await SignInAsync(name, password, cancellationToken));
        }
        finally
        {
            try
            {
                await site.Agent.SendAsync<UserRemoveResult>(HttpMethod.Post, AgentRoutes.UserRemove, new UserRemoveRequest { Name = name }, cancellationToken);
            }
            catch (NotFoundException)
            {
                // Removed by the test, or never made.
            }
        }
    }

    [SiteFact]
    public async Task A_user_opticli_didnt_make_is_never_removed()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);

        var ex = await Assert.ThrowsAnyAsync<OptiCliException>(() =>
            site.Agent.SendAsync<UserRemoveResult>(HttpMethod.Post, AgentRoutes.UserRemove, new UserRemoveRequest { Name = FixtureUser, DryRun = true }, cancellationToken));

        if (ex is NotFoundException)
        {
            // Not the edge-case site (no UsersFixture.cs).
            return;
        }
        Assert.Equal(ErrorCode.Refused, ex.Code);
        Assert.Contains("wasn't made by opticli", ex.Message);
    }

    private static Task<UserAddResult> Add(SiteUnderTest site, string name, string password, bool dryRun, CancellationToken cancellationToken) =>
        site.Agent.SendAsync<UserAddResult>(HttpMethod.Post, AgentRoutes.UserAdd, new UserAddRequest { Name = name, Password = password, DryRun = dryRun }, cancellationToken);

    /// <summary>Fills in the CMS's login page (<c>/util/login</c>) as a browser would.</summary>
    /// <returns>True when the site signed the user in (a redirect with the Identity cookie).</returns>
    private static async Task<bool> SignInAsync(string user, string password, CancellationToken cancellationToken)
    {
        var environment = OptiCliEnvironment.FromProcess();
        var state = StateStore.For(environment, SiteSettings.ProjectDirectory!).Read() ?? throw new InvalidOperationException("opticli serve isn't running.");
        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = state.BaseUrl };
        var page = await http.GetStringAsync("util/login", cancellationToken);
        var fields = Inputs().Matches(page)
            .Select(input => (Name: Attribute(input.Value, "name"), Value: Attribute(input.Value, "value") ?? ""))
            .Where(f => f.Name is not null && f.Name != "Submit")
            .ToDictionary(f => f.Name!, f => f.Value);
        fields["UserName"] = user;
        fields["Password"] = password;
        var action = FormAction().Match(page) is { Success: true } form ? WebUtility.HtmlDecode(form.Groups[1].Value) : "util/login";
        using var response = await http.PostAsync(action, new FormUrlEncodedContent(fields), cancellationToken);
        return response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther
            && cookies.GetCookies(state.BaseUrl).Any(c => c.Name.StartsWith(".AspNetCore.Identity.Application", StringComparison.Ordinal));
    }

    private static string? Attribute(string input, string name) =>
        Regex.Match(input, $"\\b{name}=\"([^\"]*)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    [GeneratedRegex("<input[^>]*>")]
    private static partial Regex Inputs();

    [GeneratedRegex("<form[^>]*action=\"([^\"]*)\"")]
    private static partial Regex FormAction();
}
