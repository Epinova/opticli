using System.Globalization;
using OptiCli.Agent.Sites;
using OptiCli.Cms;
using OptiCli.Protocol;
using Microsoft.Extensions.DependencyInjection;
#if CMS13
using System.ComponentModel.DataAnnotations;
using EPiServer.Applications;
using EPiServer.Validation;
using OptiCli.Core.Text;
#else
using EPiServer.Web;
#endif

namespace OptiCli.Agent.Compat;

/// <summary>The CMS refused a site's hosts, while they were set or on save; the message is the CMS's.</summary>
internal sealed class SiteRefusedException(string message, Exception inner) : Exception(message, inner);

/// <summary>A writable copy of a site with its planned hosts, not saved yet (<see cref="SiteStore.Prepare"/>).</summary>
internal sealed class PreparedSite(PlannedSite site, object copy)
{
    public PlannedSite Site { get; } = site;

    /// <summary>The CMS's own writable copy: a <c>SiteDefinition</c> (CMS 12) or an <c>Application</c> (CMS 13).</summary>
    internal object Copy { get; } = copy;
}

/// <summary>
/// The sites as <c>sites primary</c> and <c>sites host</c> read and save them, in the planner's shape
/// (<see cref="SiteState"/>): CMS 12's site definitions (<c>ISiteDefinitionRepository</c>), or CMS 13's routable
/// applications (<c>IApplicationRepository</c>; its site definition API is an obsolete shim that refuses to save). Both
/// save through the CMS, which clears its cache and raises its change events (to other servers too, where remote events
/// are set up).
/// </summary>
internal sealed class SiteStore(IServiceProvider services)
{
    /// <summary>The sites are CMS 13 applications: the planner's rules for those apply (<see cref="SiteHostPlanner"/>).</summary>
    public static bool Applications => AgentBuild.CmsMajor >= 13;

    /// <summary>What the sites are called in messages: site definitions, or applications.</summary>
    public static string What => Applications ? "applications" : "site definitions";

#if CMS13
    private readonly IApplicationRepository _repository = services.GetRequiredService<IApplicationRepository>();

    public IReadOnlyList<SiteState> List() => _repository.List().Where(a => a is IRoutableApplication).Select(ToState).ToList();

    /// <exception cref="AgentException"><c>not_found</c>: the application was deleted meanwhile.</exception>
    /// <exception cref="SiteRefusedException">The CMS refused a host's name or language.</exception>
    public PreparedSite Prepare(PlannedSite site)
    {
        var application = (_repository.Get(site.After.Key) ?? throw Gone(site)).CreateWritableClone();
        var routable = (IRoutableApplication)application;
        try
        {
            routable.Hosts.Clear();
            foreach (var host in site.After.Hosts.Where(h => !SiteHostPlanner.IsDefaultApplication(h)))
            {
                routable.Hosts.Add(new ApplicationHost(host.Name)
                {
                    Type = ToType(host.Type),
                    Locale = host.Language is null ? null : CultureInfo.GetCultureInfo(host.Language),
                    PreferredUrlScheme = host.Https == true ? UrlScheme.Https : UrlScheme.Http,
                });
            }
        }
        catch (ArgumentException ex)
        {
            throw new SiteRefusedException(ex.Message, ex);
        }
        return new PreparedSite(site, application);
    }

    /// <returns>The application as the repository has it after the save.</returns>
    /// <exception cref="SiteRefusedException">The CMS's validation refused it.</exception>
    public async Task<SiteState> SaveAsync(PreparedSite prepared, CancellationToken cancellationToken)
    {
        var application = (Application)prepared.Copy;
        var routable = (IRoutableApplication)application;
        var makeDefault = prepared.Site.After.Hosts.Any(SiteHostPlanner.IsDefaultApplication);
        try
        {
            if (makeDefault != routable.IsDefault)
            {
                // IsDefault has no public setter: this saves the copy (hosts and all) with it, and, when it makes this one
                // the default, the one that was (the planner only allows that when there is none).
                await _repository.MakeDefaultAsync(routable, makeDefault, cancellationToken);
            }
            else
            {
                await _repository.SaveAsync(application, cancellationToken);
            }
        }
        catch (ValidationException ex)
        {
            // The exception's message is the first error; each property's error is in Data.
            var messages = new[] { ex.Message }.Concat(ex.Data.Values.OfType<ValidationError>().Select(e => e.ErrorMessage)).Distinct().ToList();
            throw new SiteRefusedException(string.Join(" ", messages), ex);
        }
        catch (ArgumentException ex)
        {
            throw new SiteRefusedException(ex.Message, ex);
        }
        return ToState(_repository.Get(application.Name) ?? application);
    }

    internal static SiteState ToState(Application application)
    {
        var routable = (IRoutableApplication)application;
        var hosts = routable.Hosts.Select(h => new SiteHost(
            h.Authority,
            TypeName(h.Type),
            h.Locale is { Name.Length: > 0 } locale ? locale.Name : null,
            h.PreferredUrlScheme == UrlScheme.Https)).ToList();
        if (routable.IsDefault)
        {
            hosts.Add(new SiteHost(HostNames.Wildcard, HostTypes.Undefined, null, null));
        }
        return new SiteState(
            application.Name,
            string.IsNullOrWhiteSpace(application.DisplayName) ? application.Name : application.DisplayName,
            routable.Url?.ToString(),
            hosts);
    }

    private static string TypeName(ApplicationHostType type) => type switch
    {
        ApplicationHostType.Primary => HostTypes.Primary,
        ApplicationHostType.Preview => HostTypes.Preview,
        ApplicationHostType.RedirectPermanent => HostTypes.RedirectPermanent,
        ApplicationHostType.RedirectTemporary => HostTypes.RedirectTemporary,
        ApplicationHostType.Edit => HostTypes.Edit,
        ApplicationHostType.Media => HostTypes.Media,
        _ => HostTypes.Undefined,
    };

    private static ApplicationHostType ToType(string type) => type switch
    {
        HostTypes.Primary => ApplicationHostType.Primary,
        HostTypes.Preview => ApplicationHostType.Preview,
        HostTypes.RedirectPermanent => ApplicationHostType.RedirectPermanent,
        HostTypes.RedirectTemporary => ApplicationHostType.RedirectTemporary,
        HostTypes.Edit => ApplicationHostType.Edit,
        HostTypes.Media => ApplicationHostType.Media,
        _ => ApplicationHostType.Default,
    };
#else
    private readonly ISiteDefinitionRepository _repository = services.GetRequiredService<ISiteDefinitionRepository>();

    public IReadOnlyList<SiteState> List() => _repository.List().Select(ToState).ToList();

    /// <exception cref="AgentException"><c>not_found</c>: the site was deleted meanwhile.</exception>
    /// <exception cref="SiteRefusedException">The CMS refused a host's name or language, or the SiteUrl.</exception>
    public PreparedSite Prepare(PlannedSite site)
    {
        var definition = (_repository.Get(site.After.Id!.Value) ?? throw Gone(site)).CreateWritableClone();
        try
        {
            definition.Hosts = site.After.Hosts.Select(ToDefinition).ToList();
            if (site.After.SiteUrl is { } url)
            {
                definition.SiteUrl = new Uri(url);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            throw new SiteRefusedException(ex.Message, ex);
        }
        return new PreparedSite(site, definition);
    }

    /// <returns>The site as the repository has it after the save.</returns>
    /// <exception cref="SiteRefusedException">The CMS refused it (<c>ISiteDefinitionRepository.Save</c>'s checks).</exception>
    public Task<SiteState> SaveAsync(PreparedSite prepared, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var definition = (SiteDefinition)prepared.Copy;
        try
        {
            _repository.Save(definition);
        }
        catch (ArgumentException ex)
        {
            throw new SiteRefusedException(ex.Message, ex);
        }
        return Task.FromResult(ToState(_repository.Get(definition.Id) ?? definition));
    }

    internal static SiteState ToState(SiteDefinition site) => new(
        site.Id,
        site.Name,
        site.SiteUrl?.ToString(),
        site.Hosts.Select(h => new SiteHost(
            h.Name,
            HostTypes.FromValue((int)h.Type),
            h.Language is { Name.Length: > 0 } language ? language.Name : null,
            h.UseSecureConnection)).ToList());

    private static HostDefinition ToDefinition(SiteHost host) => new()
    {
        Name = host.Name,
        Type = (HostDefinitionType)HostTypes.ToValue(host.Type),
        Language = host.Language is null ? null : CultureInfo.GetCultureInfo(host.Language),
        UseSecureConnection = host.Https,
    };
#endif

    private static AgentException Gone(PlannedSite site) =>
        AgentException.NotFound($"The site {site.After.Name} was deleted meanwhile; nothing was saved.");
}
