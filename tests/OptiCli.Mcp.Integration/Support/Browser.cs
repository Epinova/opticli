using System.Net;
using System.Text.RegularExpressions;

namespace OptiCli.Mcp.Integration.Support;

/// <summary>A page the scripted browser didn't expect, such as the role gate's "No access".</summary>
public sealed class PageException(HttpStatusCode status, string title, string text)
    : Exception($"{(int)status} {title}: {text}")
{
    public HttpStatusCode Status { get; } = status;

    public string Title { get; } = title;
}

/// <summary>
/// The part of an OAuth sign-in a person does in the browser: the site's own login form (the CMS's ASP.NET Identity
/// login on Alloy), then the module's consent page. One per editor, with that editor's cookies, so it also serves the
/// connections page.
/// </summary>
internal sealed partial class Browser : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _user;
    private readonly string _password;

    public Browser(Uri site, string user, string password)
    {
        _http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = site };
        _user = user;
        _password = password;
    }

    /// <summary>What to press on the consent page.</summary>
    public string Decision { get; set; } = "allow";

    /// <summary>The last consent page shown, to check what it offered.</summary>
    public string? LastConsentPage { get; private set; }

    /// <summary>How many times the login form was filled in.</summary>
    public int SignIns { get; private set; }

    /// <summary>Follows <paramref name="authorize"/> through sign-in and consent to the client's redirect URI.</summary>
    /// <returns>The redirect back to the client, with its code or error.</returns>
    /// <exception cref="PageException">A page that is neither, such as an error page.</exception>
    public async Task<Uri> AuthorizeAsync(Uri authorize, Uri redirect, CancellationToken cancellationToken)
    {
        var url = authorize;
        for (var hop = 0; hop < 10; hop++)
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther)
            {
                url = new Uri(url, response.Headers.Location!);
                if (url.GetLeftPart(UriPartial.Path) == redirect.GetLeftPart(UriPartial.Path))
                {
                    return url;
                }
                continue;
            }
            if (html.Contains("name=\"Password\"", StringComparison.Ordinal))
            {
                url = await SignInAsync(url, html, cancellationToken);
                continue;
            }
            if (html.Contains("name=\"decision\"", StringComparison.Ordinal))
            {
                LastConsentPage = html;
                var fields = Inputs(html);
                fields["decision"] = Decision;
                using var post = await _http.PostAsync(url, new FormUrlEncodedContent(fields), cancellationToken);
                return post.Headers.Location is { } location
                    ? new Uri(url, location)
                    : throw Unexpected(post.StatusCode, await post.Content.ReadAsStringAsync(cancellationToken));
            }
            throw Unexpected(response.StatusCode, html);
        }
        throw new InvalidOperationException("Too many redirects.");
    }

    /// <summary>Signs in on the site's login form, as the CMS's login page would be filled in.</summary>
    private async Task<Uri> SignInAsync(Uri page, string html, CancellationToken cancellationToken)
    {
        SignIns++;
        var fields = Inputs(html);
        fields["UserName"] = _user;
        fields["Password"] = _password;
        if (ReturnUrl().Match(page.Query) is { Success: true } returnUrl)
        {
            fields["ReturnUrl"] = Uri.UnescapeDataString(returnUrl.Groups[1].Value);
        }
        var action = FormAction().Match(html) is { Success: true } form ? WebUtility.HtmlDecode(form.Groups[1].Value) : page.PathAndQuery;
        using var post = await _http.PostAsync(new Uri(page, action), new FormUrlEncodedContent(fields), cancellationToken);
        return post.Headers.Location is { } location
            ? new Uri(page, location)
            : throw Unexpected(post.StatusCode, await post.Content.ReadAsStringAsync(cancellationToken), $"Signing in as {_user} failed");
    }

    /// <summary>The connections page (<c>/episerver/opticli/connections</c>), signing in first if needed.</summary>
    public async Task<string> ConnectionsAsync(CancellationToken cancellationToken)
    {
        var url = new Uri(_http.BaseAddress!, "episerver/opticli/connections");
        for (var hop = 0; hop < 5; hop++)
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.Headers.Location is { } location)
            {
                url = new Uri(url, location);
                continue;
            }
            if (html.Contains("name=\"Password\"", StringComparison.Ordinal))
            {
                url = await SignInAsync(url, html, cancellationToken);
                continue;
            }
            return response.StatusCode == HttpStatusCode.OK ? html : throw Unexpected(response.StatusCode, html);
        }
        throw new InvalidOperationException("Too many redirects.");
    }

    /// <summary>Presses Revoke on every connection of <paramref name="clientId"/> on the connections page.</summary>
    /// <returns>How many were revoked.</returns>
    public async Task<int> RevokeAsync(string clientId, CancellationToken cancellationToken)
    {
        var revoked = 0;
        var html = await ConnectionsAsync(cancellationToken);
        foreach (Match row in Row().Matches(html))
        {
            if (!row.Value.Contains($"<code>{WebUtility.HtmlEncode(clientId)}</code>", StringComparison.Ordinal))
            {
                continue;
            }
            var action = WebUtility.HtmlDecode(FormAction().Match(row.Value).Groups[1].Value);
            using var post = await _http.PostAsync(new Uri(_http.BaseAddress!, action), new FormUrlEncodedContent(Inputs(row.Value)), cancellationToken);
            if (post.StatusCode != HttpStatusCode.SeeOther)
            {
                throw Unexpected(post.StatusCode, await post.Content.ReadAsStringAsync(cancellationToken), "Revoke failed");
            }
            revoked++;
        }
        return revoked;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The form's fields as the browser would send them: every input but the buttons.</summary>
    private static Dictionary<string, string> Inputs(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match input in Input().Matches(html))
        {
            string? Attribute(string name) =>
                Regex.Match(input.Value, $"\\b{name}=\"([^\"]*)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
            if (Attribute("name") is { } key && Attribute("type") is not ("submit" or "checkbox"))
            {
                fields.TryAdd(key, Attribute("value") ?? "");
            }
        }
        return fields;
    }

    private static PageException Unexpected(HttpStatusCode status, string html, string? context = null)
    {
        var title = WebUtility.HtmlDecode(Heading().Match(html).Groups[1].Value);
        var text = Regex.Replace(Regex.Replace(Regex.Replace(html, "<(style|script|head)[^>]*>.*?</\\1>", "", RegexOptions.Singleline), "<[^>]+>", " "), "\\s+", " ").Trim();
        text = WebUtility.HtmlDecode(text);
        return new PageException(status, context is null ? title : $"{context}: {title}", text.Length > 400 ? text[..400] : text);
    }

    [GeneratedRegex("ReturnUrl=([^&]+)")]
    private static partial Regex ReturnUrl();

    [GeneratedRegex("<form\\b[^>]*\\baction=\"([^\"]*)\"")]
    private static partial Regex FormAction();

    [GeneratedRegex("<input\\b[^>]*>")]
    private static partial Regex Input();

    [GeneratedRegex("<h1>(.*?)</h1>", RegexOptions.Singleline)]
    private static partial Regex Heading();

    [GeneratedRegex("<tr>.*?</tr>", RegexOptions.Singleline)]
    private static partial Regex Row();
}
