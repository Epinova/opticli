using System.Net;
using System.Text;

namespace OptiCli.Cms.Content;

/// <summary>
/// Finds script in rich text (XhtmlString), in SVG files, and links that aren't web, mail or phone links, for an editor's
/// writes (<see cref="CmsCall.MayWriteScript"/>). EPiServer-free: the CMS's own script parser only exists from CMS 12.15,
/// and keeps script by default.
/// </summary>
/// <remarks>
/// <para>
/// The markup is read the way a browser's HTML tokenizer reads it, so that nothing a browser would take for a tag hides
/// from the check: tag and attribute names in any case, attributes without quotes or without whitespace between them
/// (<c>&lt;img/src=x"onerror=..."&gt;</c>), comments as the browser ends them, the text of <c>&lt;style&gt;</c> (which
/// is read both as text and as markup), and URLs with character references, tabs, line breaks or leading control
/// characters in their scheme (<c>jav&amp;#x61;script:</c>, <c>java&amp;Tab;script:</c>). Elements whose content a
/// browser reads by other rules (<c>&lt;textarea&gt;</c>, <c>&lt;noscript&gt;</c>, <c>&lt;svg&gt;</c>, ...) aren't
/// allowed at all, and neither is markup that ends inside an unfinished tag.
/// </para>
/// <para>
/// It refuses rather than strips, so the user learns what was wrong. Ordinary TinyMCE markup passes: tables, images
/// (also <c>data:image/</c> ones), links, embedded blocks, and <c>style</c> and <c>class</c> attributes.
/// </para>
/// </remarks>
internal static class MarkupSafety
{
    /// <summary>Elements that run script, load other documents, submit or redirect, or change how the browser reads what follows.</summary>
    private static readonly HashSet<string> RefusedElements = new(StringComparer.Ordinal)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "base", "meta", "link",
        "noscript", "xmp", "noembed", "noframes", "plaintext", "textarea", "title", "svg", "math",
    };

    /// <summary>Attributes whose value a browser loads or navigates to; any other <c>*:href</c> (<c>xlink:href</c>) too.</summary>
    private static readonly HashSet<string> UrlAttributes = new(StringComparer.Ordinal)
    {
        "href", "src", "action", "formaction", "poster", "background", "lowsrc", "dynsrc", "data", "codebase", "cite",
        "longdesc", "usemap", "ping", "icon", "manifest", "archive", "classid", "srcset", "imagesrcset",
    };

    /// <summary>Schemes a browser runs as script.</summary>
    private static readonly HashSet<string> ScriptSchemes = new(StringComparer.Ordinal) { "javascript", "vbscript", "livescript" };

    /// <summary>The schemes a link (a link property, a shortcut) may have; one without a scheme is relative and fine.</summary>
    public static readonly IReadOnlyList<string> LinkSchemes = ["http", "https", "mailto", "tel"];

    /// <summary>HTML5 character references a browser decodes that matter for a URL's scheme, and that <see cref="WebUtility.HtmlDecode(string)"/> doesn't know.</summary>
    private static readonly Dictionary<string, string> Html5References = new(StringComparer.Ordinal)
    {
        ["colon"] = ":", ["Tab"] = "\t", ["NewLine"] = "\n", ["period"] = ".", ["plus"] = "+", ["sol"] = "/",
        ["quest"] = "?", ["num"] = "#", ["semi"] = ";", ["excl"] = "!", ["lpar"] = "(", ["rpar"] = ")", ["comma"] = ",",
    };

    /// <summary>Stands for a character reference that couldn't be read: whatever a browser makes of it, it is no scheme character of its own.</summary>
    private const char Unreadable = '￿';

    /// <summary>What in <paramref name="html"/> could run script, e.g. "an event handler attribute 'onerror' on &lt;img&gt;"; null when nothing.</summary>
    public static string? Script(string html) => Scripts(html).FirstOrDefault()?.Problem;

    /// <summary>
    /// Something that could run script: what it is (<see cref="Problem"/>), and the construct itself (<see cref="Key"/>):
    /// the whole tag as read, or the link with all its attributes. Two findings with the same key are the same construct,
    /// so a value written back can be compared with the one it replaces, construct by construct.
    /// </summary>
    /// <param name="Live">
    /// Whether a browser surely reads it as a tag of the document: not inside an element whose content it reads as text
    /// (<c>&lt;textarea&gt;</c>, <c>&lt;style&gt;</c>, ...) or keeps inert or parses by other rules
    /// (<c>&lt;template&gt;</c>, <c>&lt;svg&gt;</c>, ...), nor after a CDATA section. Only a live construct counts as
    /// one a value already has: one that was inert in it doesn't become one that may be written live.
    /// </param>
    internal sealed record Finding(string Key, string Problem, bool Live = true);

    /// <summary>Every tag in <paramref name="html"/> that could run script (<see cref="Script"/>), in order, one finding per tag.</summary>
    public static IReadOnlyList<Finding> Scripts(string html)
    {
        var findings = new List<Finding>();
        foreach (var tag in Tags(html))
        {
            // A browser ignores an end tag's attributes.
            var problem = tag.EndTag ? null
                : RefusedElements.Contains(tag.Element) ? $"a <{tag.Element}> element"
                : tag.Attributes.Select(a => AttributeProblem(tag.Element, a.Name, a.Value)).FirstOrDefault(p => p is not null);
            problem ??= tag.Closed ? null : $"an unfinished <{(tag.EndTag ? "/" : "")}{tag.Element}> tag";
            if (problem is not null)
            {
                findings.Add(new Finding(tag.Key, problem, !tag.Inert));
            }
        }
        return findings;
    }

    /// <summary>
    /// What in an SVG image could run script when the browser shows it on the site's own origin (opened as a file, not
    /// in an <c>&lt;img&gt;</c>): <c>&lt;script&gt;</c> (in any namespace), <c>&lt;foreignObject&gt;</c> (which can hold
    /// HTML), event handler attributes, <c>javascript:</c> or non-image <c>data:</c> links, also in animations that set
    /// one; entity declarations (whose text could be markup) and <c>xml-stylesheet</c> instructions (XSLT). Null for an
    /// ordinary drawing.
    /// </summary>
    public static string? Svg(string svg)
    {
        if (svg.Contains('\0'))
        {
            return "NUL characters, so it may not be the text it seems";
        }
        if (svg.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            return "an entity declaration, whose text could be markup";
        }
        if (svg.Contains("<!ATTLIST", StringComparison.OrdinalIgnoreCase))
        {
            return "an attribute list declaration, which could give elements attributes they don't show";
        }
        if (svg.Contains("<?xml-stylesheet", StringComparison.OrdinalIgnoreCase))
        {
            return "an xml-stylesheet instruction";
        }
        foreach (var tag in Tags(svg, xml: true))
        {
            if (tag.EndTag)
            {
                continue;
            }
            // XML: a prefix (<svg:script>) doesn't make it another element.
            if (SvgRefusedElements.Contains(LocalName(tag.Element)))
            {
                return $"a <{tag.Element}> element";
            }
            foreach (var (name, value) in tag.Attributes)
            {
                var local = LocalName(name);
                if (local.StartsWith("on", StringComparison.Ordinal))
                {
                    return $"an event handler attribute '{name}' on <{tag.Element}>";
                }
                if (value is null)
                {
                    continue;
                }
                if (local is "href" or "src" or "base" && UrlProblem(tag.Element, name, value) is { } url)
                {
                    return url;
                }
                if (local is "to" or "from" or "values" or "by"
                    && value.Split(';').Select(Scheme).FirstOrDefault(s => s is { Unreadable: true } || (s is not null && ScriptSchemes.Contains(s.Name))) is { } animated)
                {
                    return $"an animation of <{tag.Element}> to a {(animated.Unreadable ? "URL that can't be read" : animated.Name + ": URL")}";
                }
            }
            if (!tag.Closed)
            {
                return $"an unfinished <{tag.Element}> tag";
            }
        }
        return null;
    }

    /// <summary>
    /// <see cref="Svg"/> for a file: its text as a browser reads it, UTF-8 or (with a byte order mark) UTF-16 or UTF-32,
    /// or a single-byte encoding it declares. A file in an encoding that couldn't be read as a browser reads it is refused.
    /// </summary>
    public static string? SvgFile(byte[] data)
    {
        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(data, writable: false), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: fine for a single-byte encoding it declares, whose markup characters are ASCII.
            text = Encoding.Latin1.GetString(data);
            if (DeclaredEncoding(text) is not { } single || !(single.StartsWith("iso-8859-", StringComparison.Ordinal) || single.StartsWith("windows-125", StringComparison.Ordinal) || single == "latin1"))
            {
                return "text in an encoding that can't be checked";
            }
        }
        if (DeclaredEncoding(text) is { } declared && !(declared is "utf-8" or "utf8" or "us-ascii" or "ascii" or "utf-16" or "utf-32" or "latin1"
            || declared.StartsWith("iso-8859-", StringComparison.Ordinal) || declared.StartsWith("windows-125", StringComparison.Ordinal)))
        {
            return $"an encoding that can't be checked ({declared})";
        }
        return Svg(text);
    }

    /// <summary>The encoding an XML declaration names, in lower case; null without one.</summary>
    private static string? DeclaredEncoding(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, "^\\uFEFF?\\s*<\\?xml\\b[^>]*?\\bencoding\\s*=\\s*[\"']([^\"']*)[\"']");
        return match.Success ? match.Groups[1].Value.Trim().ToLowerInvariant() : null;
    }

    /// <summary>Elements an SVG file may not have (by local name): script, HTML inside it, and what loads other documents.</summary>
    private static readonly HashSet<string> SvgRefusedElements = new(StringComparer.Ordinal)
    {
        "script", "foreignobject", "handler", "listener", "iframe", "frame", "object", "embed", "applet", "form", "base", "meta", "link",
    };

    /// <summary>A name without its namespace prefix.</summary>
    private static string LocalName(string name) => name.LastIndexOf(':') is var colon and >= 0 ? name[(colon + 1)..] : name;

    /// <summary>
    /// The content an embedded block in rich text names, as given: each <c>data-contentlink</c> (an id) and
    /// <c>data-contentguid</c> attribute's value, whatever the element. The CMS fills in the other form itself, so what
    /// the request named is only known from the markup.
    /// </summary>
    public static IReadOnlyList<(string Attribute, string Value)> ContentReferences(string html) =>
    [
        .. Tags(html).Where(t => !t.EndTag).SelectMany(t => t.Attributes)
            .Where(a => a is { Name: "data-contentlink" or "data-contentguid", Value: not null })
            .Select(a => (a.Name, a.Value!)),
    ];

    /// <summary>A tag as a browser's tokenizer reads it: names in lower case, every attribute (a value is null without '=').</summary>
    /// <param name="Closed">False for a tag the markup ends in the middle of.</param>
    /// <param name="Inert">True where a browser may not read it as a live tag (<see cref="Finding.Live"/>).</param>
    private sealed record Tag(string Element, bool EndTag, IReadOnlyList<(string Name, string? Value)> Attributes, bool Closed, bool Inert)
    {
        /// <summary>The tag written out as read: the same for the same tag wherever it is, whatever its quotes or spacing.</summary>
        public string Key =>
            $"<{(EndTag ? "/" : "")}{Element}{string.Concat(Attributes.Select(a => a.Value is null ? $" {a.Name}" : $" {a.Name}=\"{a.Value}\""))}{(Closed ? ">" : "")}";
    }

    /// <summary>Elements whose content a browser reads as text up to their end tag (RCDATA, RAWTEXT, script data).</summary>
    private static readonly HashSet<string> TextElements = new(StringComparer.Ordinal)
    {
        "title", "textarea", "style", "xmp", "iframe", "noembed", "noframes", "noscript", "script",
    };

    /// <summary>
    /// Elements whose content is markup, but not surely live: a template's content is inert, older parsers drop what a
    /// select holds, SVG and MathML are parsed by other rules (CDATA sections among them), an object's or applet's content
    /// is only a fallback, and a frameset ignores most of what it holds.
    /// </summary>
    private static readonly HashSet<string> InertContainers = new(StringComparer.Ordinal)
    {
        "template", "select", "svg", "math", "object", "applet", "frameset",
    };

    /// <summary>
    /// The tags of <paramref name="html"/>, in order. Text, comments and other declarations are skipped as a browser skips
    /// them. The content of an element a browser reads as text is read as markup too, so nothing in it hides from a
    /// check, but marked <see cref="Tag.Inert"/>, as is everything in an inert container, after a CDATA section, or after
    /// <c>&lt;plaintext&gt;</c>.
    /// </summary>
    /// <param name="xml">An XML document (an SVG file): a CDATA section is text up to <c>]]&gt;</c>.</param>
    /// <param name="inert">Every tag read is inert (the content of a text element).</param>
    private static IEnumerable<Tag> Tags(string html, bool xml = false, bool inert = false)
    {
        var n = html.Length;
        var i = 0;
        var open = new List<string>();
        while (i < n)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= n)
            {
                yield break;
            }
            i = lt + 1;
            switch (html[i])
            {
                case '!' when string.CompareOrdinal(html, i + 1, "[CDATA[", 0, 7) == 0:
                    if (xml)
                    {
                        i = html.IndexOf("]]>", i, StringComparison.Ordinal) is var end and >= 0 ? end + 3 : n;
                        continue;
                    }
                    // A browser ends it at the next '>' in HTML, at ']]>' in SVG or MathML: from here on, which of its
                    // tags it reads as live can't be told.
                    inert = true;
                    i = SkipDeclaration(html, i + 1);
                    continue;
                case '!':
                    i = SkipDeclaration(html, i + 1);
                    continue;
                case '?':
                    i = After(html, i, '>');
                    continue;
            }
            var endTag = html[i] == '/';
            var nameStart = endTag ? i + 1 : i;
            if (nameStart >= n)
            {
                yield break;
            }
            if (!IsAsciiLetter(html[nameStart]))
            {
                // "</>" is dropped, "</" with anything else is a comment up to the next '>', and any other '<' is text.
                if (endTag)
                {
                    i = html[nameStart] == '>' ? nameStart + 1 : After(html, nameStart, '>');
                }
                continue;
            }
            var nameEnd = nameStart;
            while (nameEnd < n && !IsSpace(html[nameEnd]) && html[nameEnd] != '/' && html[nameEnd] != '>')
            {
                nameEnd++;
            }
            var element = AsciiLower(html[nameStart..nameEnd]);
            var (attributes, next, closed) = Attributes(html, nameEnd);
            yield return new Tag(element, endTag, attributes, closed, inert || open.Count > 0);
            if (!closed)
            {
                yield break;
            }
            i = next;
            if (endTag)
            {
                // A browser ignores an end tag for an element opened outside the template it is in: a template's
                // content stays inert up to its own end tag.
                if (open.LastIndexOf(element) is var at and >= 0 && (element == "template" || open.LastIndexOf("template") <= at))
                {
                    open.RemoveRange(at, open.Count - at);
                }
            }
            else if (TextElements.Contains(element))
            {
                // Read as text up to its end tag, by a browser; read as markup here as well, in case it isn't.
                var close = RawTextEnd(html, i, element);
                foreach (var inside in Tags(html[i..close], xml, inert: true))
                {
                    yield return inside;
                }
                i = close;
            }
            else if (element == "plaintext" && !xml)
            {
                foreach (var inside in Tags(html[i..], xml, inert: true))
                {
                    yield return inside;
                }
                yield break;
            }
            else if (InertContainers.Contains(element) && !(element is "svg" or "math" && html[next - 2] == '/'))
            {
                open.Add(element);
            }
        }
    }

    /// <summary>
    /// Why <paramref name="url"/> isn't a link an editor may set: its scheme isn't one of <see cref="LinkSchemes"/>
    /// (<c>javascript:</c>, <c>data:</c>, ...); null for an http, https, mailto or tel link, a relative one, a stored
    /// permanent link (<c>~/link/...</c>) or an anchor. A control character anywhere in it is refused too.
    /// </summary>
    public static string? Link(string url)
    {
        if (ControlCharacter(url) is { } control)
        {
            return $"a control character (U+{(int)control:X4}) in a URL";
        }
        return Scheme(url) switch
        {
            null => null,
            { Unreadable: true } => "a URL whose scheme has a character reference that can't be read",
            { Name: var scheme } when LinkSchemes.Contains(scheme) => null,
            { Name: var scheme } => $"a {scheme}: link (only {string.Join(", ", LinkSchemes)} or relative links)",
        };
    }

    /// <summary>
    /// The first control character (U+0000 to U+001F, U+007F) in <paramref name="url"/>, also one written as a character
    /// reference; null for none. A browser drops some of them, and reads a scheme with others in it as no scheme, while
    /// the CMS can't store some at all: such a link means something else to each.
    /// </summary>
    private static char? ControlCharacter(string url) =>
        url.Concat(Decode(url)).Where(c => c < ' ' || c == '\u007F').Select(c => (char?)c).FirstOrDefault();

    private sealed record UrlScheme(string Name, string Rest, bool Unreadable);

    /// <summary>
    /// The scheme a browser finds in <paramref name="value"/>, an attribute value or a link: character references
    /// decoded, tabs and line breaks removed, leading and trailing control characters and spaces trimmed. Null when it
    /// has none (a relative URL).
    /// </summary>
    private static UrlScheme? Scheme(string value)
    {
        var url = Decode(value).Replace("\t", "").Replace("\n", "").Replace("\r", "").Trim(Controls);
        var end = 0;
        while (end < url.Length && (IsAsciiLetter(url[end]) || (end > 0 && (char.IsAsciiDigit(url[end]) || url[end] is '+' or '-' or '.'))))
        {
            end++;
        }
        if (end < url.Length && url[end] == Unreadable)
        {
            return new UrlScheme("", "", Unreadable: true);
        }
        return end > 0 && end < url.Length && url[end] == ':'
            ? new UrlScheme(AsciiLower(url[..end]), url[(end + 1)..], Unreadable: false)
            : null;
    }

    private static readonly char[] Controls = [.. Enumerable.Range(0, 0x21).Select(c => (char)c)];

    /// <summary>A URL attribute's problem: a scheme that runs script, or a <c>data:</c> URL that isn't an image (an SVG image may hold script).</summary>
    private static string? UrlProblem(string element, string attribute, string value)
    {
        var scheme = Scheme(value);
        if (scheme is null)
        {
            return null;
        }
        if (scheme.Unreadable)
        {
            return $"a URL in '{attribute}' on <{element}> whose scheme has a character reference that can't be read";
        }
        if (ScriptSchemes.Contains(scheme.Name))
        {
            return $"a {scheme.Name}: URL in '{attribute}' on <{element}>";
        }
        var type = AsciiLower(scheme.Rest.TrimStart(Controls));
        return scheme.Name == "data" && (!type.StartsWith("image/", StringComparison.Ordinal) || type.StartsWith("image/svg", StringComparison.Ordinal))
            ? $"a data: URL in '{attribute}' on <{element}> that isn't an image"
            : null;
    }

    /// <summary>What an attribute could run: an event handler, an <c>srcdoc</c> document, or a URL with a script scheme.</summary>
    private static string? AttributeProblem(string element, string name, string? value)
    {
        if (name.StartsWith("on", StringComparison.Ordinal))
        {
            return $"an event handler attribute '{name}' on <{element}>";
        }
        if (name == "srcdoc")
        {
            return $"an srcdoc attribute on <{element}>";
        }
        if (value is null || !(UrlAttributes.Contains(name) || name.EndsWith(":href", StringComparison.Ordinal)))
        {
            return null;
        }
        if (name is "srcset" or "imagesrcset")
        {
            // Candidates are "url descriptor", separated by commas.
            return value.Split(',').Select(c => c.Trim(Controls)).Where(c => c.Length > 0)
                .Select(c => UrlProblem(element, name, c.Split(' ', '\t', '\n', '\r', '\f')[0]))
                .FirstOrDefault(p => p is not null);
        }
        return UrlProblem(element, name, value);
    }

    /// <summary>A tag's attributes, as the tokenizer reads them from just after its name.</summary>
    /// <returns>The attributes; where the tag ends; and whether it ended at all.</returns>
    private static (List<(string Name, string? Value)> Attributes, int Next, bool Closed) Attributes(string html, int i)
    {
        var attributes = new List<(string Name, string? Value)>();
        var n = html.Length;
        while (true)
        {
            while (i < n && (IsSpace(html[i]) || html[i] == '/'))
            {
                i++;
            }
            if (i >= n)
            {
                return (attributes, n, false);
            }
            if (html[i] == '>')
            {
                return (attributes, i + 1, true);
            }
            // The first character may be '=': it is then part of the name.
            var start = i++;
            while (i < n && !IsSpace(html[i]) && html[i] is not ('/' or '>' or '='))
            {
                i++;
            }
            var name = AsciiLower(html[start..i]);
            var k = i;
            while (k < n && IsSpace(html[k]))
            {
                k++;
            }
            string? value = null;
            if (k < n && html[k] == '=')
            {
                k++;
                while (k < n && IsSpace(html[k]))
                {
                    k++;
                }
                if (k >= n)
                {
                    attributes.Add((name, ""));
                    return (attributes, n, false);
                }
                if (html[k] is '"' or '\'')
                {
                    var close = html.IndexOf(html[k], k + 1);
                    if (close < 0)
                    {
                        attributes.Add((name, html[(k + 1)..]));
                        return (attributes, n, false);
                    }
                    value = html[(k + 1)..close];
                    k = close + 1;
                }
                else
                {
                    var valueStart = k;
                    while (k < n && !IsSpace(html[k]) && html[k] != '>')
                    {
                        k++;
                    }
                    value = html[valueStart..k];
                }
            }
            attributes.Add((name, value));
            i = k;
        }
    }

    /// <summary>
    /// Past a markup declaration (just after <c>&lt;!</c>): a comment ends where a browser ends it (<c>--&gt;</c>,
    /// <c>--!&gt;</c>, or at once for <c>&lt;!--&gt;</c> and <c>&lt;!---&gt;</c>), anything else (a doctype, CDATA
    /// outside SVG) at the next <c>&gt;</c>.
    /// </summary>
    private static int SkipDeclaration(string html, int i)
    {
        if (string.CompareOrdinal(html, i, "--", 0, 2) != 0)
        {
            return After(html, i, '>');
        }
        var body = i + 2;
        if (string.CompareOrdinal(html, body, ">", 0, 1) == 0)
        {
            return body + 1;
        }
        if (string.CompareOrdinal(html, body, "->", 0, 2) == 0)
        {
            return body + 2;
        }
        var plain = html.IndexOf("-->", body, StringComparison.Ordinal);
        var bang = html.IndexOf("--!>", body, StringComparison.Ordinal);
        return (plain, bang) switch
        {
            (< 0, < 0) => html.Length,
            (< 0, _) => bang + 4,
            (_, < 0) => plain + 3,
            _ => Math.Min(plain + 3, bang + 4),
        };
    }

    /// <summary>Where the text of a raw-text element ends: at its end tag (<c>&lt;/style</c> then a space, '/' or '&gt;'), or the end.</summary>
    private static int RawTextEnd(string html, int i, string element)
    {
        var closing = "</" + element;
        for (var at = html.IndexOf(closing, i, StringComparison.OrdinalIgnoreCase); at >= 0; at = html.IndexOf(closing, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = at + closing.Length;
            if (after >= html.Length || IsSpace(html[after]) || html[after] is '/' or '>')
            {
                return at;
            }
        }
        return html.Length;
    }

    /// <summary>
    /// Character references decoded as a browser decodes them in an attribute value: numeric ones with or without the
    /// semicolon, named ones it knows. A named one with a semicolon that isn't known here becomes <see cref="Unreadable"/>:
    /// HTML has over two thousand. Without the semicolon only legacy names (<c>&amp;amp</c>, <c>&amp;copy</c>) are decoded,
    /// and none of those decodes to a character a scheme could use, so they are left as they are.
    /// </summary>
    private static string Decode(string value)
    {
        if (!value.Contains('&'))
        {
            return value;
        }
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '&')
            {
                result.Append(value[i]);
                continue;
            }
            var j = i + 1;
            if (j < value.Length && value[j] == '#')
            {
                var hex = j + 1 < value.Length && value[j + 1] is 'x' or 'X';
                var digits = hex ? j + 2 : j + 1;
                var end = digits;
                while (end < value.Length && (hex ? char.IsAsciiHexDigit(value[end]) : char.IsAsciiDigit(value[end])))
                {
                    end++;
                }
                if (end == digits)
                {
                    result.Append('&');
                    continue;
                }
                var number = value[digits..end].TrimStart('0');
                var code = number.Length == 0 ? 0 : number.Length > 7 ? int.MaxValue : Convert.ToInt32(number, hex ? 16 : 10);
                result.Append(code is 0 or > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF ? "�" : char.ConvertFromUtf32(code));
                i = end < value.Length && value[end] == ';' ? end : end - 1;
                continue;
            }
            var nameEnd = j;
            while (nameEnd < value.Length && char.IsAsciiLetterOrDigit(value[nameEnd]))
            {
                nameEnd++;
            }
            if (nameEnd == j || nameEnd >= value.Length || value[nameEnd] != ';')
            {
                result.Append('&');
                continue;
            }
            var reference = value[i..(nameEnd + 1)];
            var decoded = WebUtility.HtmlDecode(reference);
            result.Append(decoded != reference ? decoded : Html5References.TryGetValue(value[j..nameEnd], out var known) ? known : Unreadable.ToString());
            i = nameEnd;
        }
        return result.ToString();
    }

    /// <summary>Just past the next <paramref name="c"/> from <paramref name="i"/>, or the end.</summary>
    private static int After(string html, int i, char c) => html.IndexOf(c, i) is var at and >= 0 ? at + 1 : html.Length;

    /// <summary>HTML's whitespace (the tokenizer has already turned carriage returns into line feeds).</summary>
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    private static bool IsAsciiLetter(char c) => char.IsAsciiLetter(c);

    /// <summary>Lower case as a browser lowers tag and attribute names: ASCII letters only.</summary>
    private static string AsciiLower(string value) => string.Create(value.Length, value, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
        }
    });
}
