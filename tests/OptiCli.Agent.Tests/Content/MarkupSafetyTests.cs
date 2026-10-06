using OptiCli.Cms;
using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Content;

/// <summary>
/// Script in rich text and unsafe links, as an editor's writes are checked for them: whatever a browser would run is
/// found, however it is written, and the markup TinyMCE makes passes.
/// </summary>
public class MarkupSafetyTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>", "<script>")]
    [InlineData("<p>Hi</p><SCRIPT src=//evil.example/x.js></SCRIPT>", "<script>")]
    [InlineData("<ScRiPt>alert(1)</ScRiPt>", "<script>")]
    [InlineData("<img src=x onerror=alert(1)>", "'onerror'")]
    [InlineData("<img src=\"x\" ONERROR=\"alert(1)\">", "'onerror'")]
    [InlineData("<img src=\"x\"onerror=\"alert(1)\">", "'onerror'")]
    [InlineData("<img/src=\"x\"/onerror=alert(1)>", "'onerror'")]
    [InlineData("<img src=x\nonerror\n=\nalert(1)>", "'onerror'")]
    [InlineData("<svg onload=alert(1)>", "<svg>")]
    [InlineData("<body onload=alert(1)>", "'onload'")]
    [InlineData("<div onmouseover='alert(1)'>x</div>", "'onmouseover'")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=javascript:alert(1)>x</a>", "javascript:")]
    [InlineData("<a href=\"  JavaScript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"jav&#x61;script:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"jav&#97;script:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"jav&#0000097script:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"java\tscript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"java&Tab;script:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"java&#9;script:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"javascript&colon;alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"&#1;javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"javascript&unknownref;alert(1)\">x</a>", "can't be read")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">x</a>", "vbscript:")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>", "data:")]
    [InlineData("<img src=\"data:image/svg+xml;base64,PHN2Zz4=\">", "data:")]
    [InlineData("<form action=\"https://evil.example\"><button>Go</button></form>", "<form>")]
    [InlineData("<button formaction=\"javascript:alert(1)\">Go</button>", "javascript:")]
    [InlineData("<video poster=\"javascript:alert(1)\"></video>", "javascript:")]
    [InlineData("<a xlink:href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<img srcset=\"a.png 1x, javascript:alert(1) 2x\">", "javascript:")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>", "<iframe>")]
    [InlineData("<div srcdoc=\"&lt;script&gt;\"></div>", "srcdoc")]
    [InlineData("<object data=\"x.swf\"></object>", "<object>")]
    [InlineData("<embed src=\"x.swf\">", "<embed>")]
    [InlineData("<base href=\"https://evil.example/\">", "<base>")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">", "<meta>")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://evil.example/x.css\">", "<link>")]
    [InlineData("<math><mi xlink:href=\"javascript:alert(1)\">x</mi></math>", "<math>")]
    [InlineData("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\"></noscript>", "<noscript>")]
    [InlineData("<textarea><img src=x onerror=alert(1)></textarea>", "<textarea>")]
    [InlineData("<style><img src=x onerror=alert(1)></style>", "'onerror'")]
    [InlineData("<style>p{}</style><img src=x onerror=alert(1)>", "'onerror'")]
    [InlineData("<!-- a --><img src=x onerror=alert(1)>", "'onerror'")]
    [InlineData("<!--><img src=x onerror=alert(1)>", "'onerror'")]
    [InlineData("<!---><img src=x onerror=alert(1)>", "'onerror'")]
    [InlineData("<!-- a --!><img src=x onerror=alert(1)>", "'onerror'")]
    [InlineData("<!DOCTYPE html><img src=x onerror=alert(1)>", "'onerror'")]
    [InlineData("<a<img src=x onerror=alert(1)>", "'onerror'")]
    [InlineData("<p>text</p><img src=x", "unfinished")]
    [InlineData("<p title=\"open", "unfinished")]
    public void Script_is_found_however_it_is_written(string html, string found)
    {
        var problem = MarkupSafety.Script(html);

        Assert.NotNull(problem);
        Assert.Contains(found, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<p>Plain <strong>bold</strong> and <em>italic</em> text.</p>")]
    [InlineData("<h2 style=\"color: #333; text-align: center\" class=\"lead\">Heading</h2>")]
    [InlineData("<p><a href=\"https://example.com/page?a=1&amp;b=2\" target=\"_blank\" rel=\"noopener\" title=\"Example\">link</a></p>")]
    [InlineData("<p><a href=\"/en/about-us/\">relative</a> <a href=\"~/link/0f8fad5bd9cb469fa165708ab64ffc25.aspx\">permanent</a> <a href=\"#top\">anchor</a></p>")]
    [InlineData("<p><a href=\"mailto:editor@example.com\">mail</a> <a href=\"tel:+4712345678\">phone</a> <a href=\"ftp://files.example.com/a.zip\">ftp</a></p>")]
    [InlineData("<p><img src=\"/globalassets/team.jpg\" alt=\"Team\" width=\"300\" height=\"200\" style=\"float: right;\"></p>")]
    [InlineData("<p><img src=\"data:image/png;base64,iVBORw0KGgo=\" alt=\"Pasted\"></p>")]
    [InlineData("<table class=\"table\" style=\"width: 100%;\"><thead><tr><th scope=\"col\">A</th></tr></thead><tbody><tr><td colspan=\"2\">1 &lt; 2</td></tr></tbody></table>")]
    [InlineData("<div data-classid=\"36f4349b-8093-492b-b616-05d8964e4c89\" data-contentguid=\"0f8fad5b-d9cb-469f-a165-708ab64ffc25\" data-contentname=\"Teaser\">{}</div>")]
    [InlineData("<ul><li>One</li><li>Two</li></ul><ol start=\"3\"><li>Three</li></ol><blockquote cite=\"https://example.com\">Quote</blockquote>")]
    [InlineData("<p>a &amp; b, 3 &lt; 4, <br> line <br/> break, <hr /> rule</p><!-- a comment -->")]
    [InlineData("<p>Price < 100 and x <= y</p>")]
    [InlineData("<p title=\"javascript: the language\">About JavaScript</p>")]
    [InlineData("<details open><summary>More</summary><p>Text</p></details>")]
    [InlineData("")]
    public void Ordinary_rich_text_passes(string html) => Assert.Null(MarkupSafety.Script(html));

    [Fact]
    public void An_attribute_is_read_as_a_browser_reads_it_not_as_it_looks() =>
        // An unquoted value runs to the next space or '>': this is one src, as a browser has it.
        Assert.Null(MarkupSafety.Script("<img src=x/onerror=alert(1)>"));

    [Fact]
    public void The_content_embedded_blocks_name_is_read_as_the_markup_gives_it()
    {
        var references = MarkupSafety.ContentReferences(
            "<p>x</p><div data-classid=\"36f4349b-8093-492b-b616-05d8964e4c89\" data-contentlink=\"123\">{}</div>" +
            "<DIV DATA-CONTENTGUID='0f8fad5b-d9cb-469f-a165-708ab64ffc25'>{}</DIV><p data-contentname=\"Teaser\">y</p>");

        Assert.Equal([("data-contentlink", "123"), ("data-contentguid", "0f8fad5b-d9cb-469f-a165-708ab64ffc25")], references);
    }

    [Fact]
    public void A_construct_is_known_by_its_tag_wherever_it_is_and_however_it_is_quoted()
    {
        var first = MarkupSafety.Scripts("<p>a</p><iframe src='https://example.com/v' width=5></iframe>");
        var moved = MarkupSafety.Scripts("<iframe src=\"https://example.com/v\"  width=\"5\"></iframe><p>b</p>");
        var other = MarkupSafety.Scripts("<iframe src=\"https://example.com/w\" width=\"5\"></iframe>");

        Assert.Equal(first.Select(f => f.Key), moved.Select(f => f.Key));
        Assert.NotEqual(first.Single().Key, other.Single().Key);
        Assert.Equal(2, MarkupSafety.Scripts("<script>a</script><script>a</script>").Count);
    }

    [Theory]
    [InlineData("<textarea>{0}</textarea>")]
    [InlineData("<title>{0}</title>")]
    [InlineData("<noscript>{0}</noscript>")]
    [InlineData("<xmp>{0}</xmp>")]
    [InlineData("<noembed>{0}</noembed>")]
    [InlineData("<noframes>{0}</noframes>")]
    [InlineData("<style>{0}</style>")]
    [InlineData("<script>{0}</script>")]
    [InlineData("<iframe>{0}</iframe>")]
    [InlineData("<plaintext>{0}")]
    [InlineData("<template>{0}</template>")]
    [InlineData("<template><template></template>{0}</template>")]
    [InlineData("<object><template></object>{0}</template></object>")]
    [InlineData("<select><template></select>{0}</template></select>")]
    [InlineData("<select>{0}</select>")]
    [InlineData("<svg>{0}</svg>")]
    [InlineData("<math>{0}</math>")]
    [InlineData("<object data=\"x\">{0}</object>")]
    [InlineData("<![CDATA[x]]>{0}")]
    [InlineData("<svg><![CDATA[ > </svg> ]]>{0}</svg>")]
    public void What_a_browser_reads_as_text_or_keeps_inert_is_found_but_not_live(string container)
    {
        var findings = MarkupSafety.Scripts(string.Format(container, "<img src=x onerror=alert(1)>"));

        var img = Assert.Single(findings, f => f.Key.StartsWith("<img", StringComparison.Ordinal));
        Assert.False(img.Live);
        Assert.All(findings.Where(f => f != img && f.Key.StartsWith("<", StringComparison.Ordinal) && !f.Key.StartsWith("<img", StringComparison.Ordinal)),
            f => Assert.True(f.Live, f.Key));
    }

    [Fact]
    public void After_a_text_element_ends_or_an_inert_one_closes_tags_are_live_again()
    {
        Assert.True(MarkupSafety.Scripts("<textarea>a</textarea><img src=x onerror=alert(1)>").Single(f => f.Key.StartsWith("<img", StringComparison.Ordinal)).Live);
        Assert.True(MarkupSafety.Scripts("<template><b>a</b></template><img src=x onerror=alert(1)>").Single(f => f.Key.StartsWith("<img", StringComparison.Ordinal)).Live);
        Assert.True(MarkupSafety.Scripts("<svg/><img src=x onerror=alert(1)>").Single(f => f.Key.StartsWith("<img", StringComparison.Ordinal)).Live);
        // A browser ends <style> at </style, whatever comes before it.
        Assert.True(MarkupSafety.Scripts("<style>/* <!-- */</style><img src=x onerror=alert(1)>").Single(f => f.Key.StartsWith("<img", StringComparison.Ordinal)).Live);
    }

    private const string Drawing = """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- Generator: a drawing program -->
        <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
        <svg version="1.1" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100">
          <style>.a{fill:#c00}</style>
          <defs><linearGradient id="g"><stop offset="0" stop-color="#fff"/></linearGradient></defs>
          <g class="a"><path d="M0 0h100v100H0z"/><circle cx="50" cy="50" r="10" fill="url(#g)"/></g>
          <image width="10" height="10" xlink:href="data:image/png;base64,iVBORw0KGgo="/>
          <a href="https://example.com/"><text x="10" y="90">Logo &amp; name</text></a>
          <use href="#g"/><animate attributeName="r" from="10" to="20" dur="1s"/>
        </svg>
        """;

    [Fact]
    public void An_ordinary_drawing_passes_as_an_svg_file() => Assert.Null(MarkupSafety.Svg(Drawing));

    [Theory]
    [InlineData("<script>alert(document.domain)</script>", "<script>")]
    [InlineData("<svg:script xmlns:svg=\"http://www.w3.org/2000/svg\">alert(1)</svg:script>", "<svg:script>")]
    [InlineData("<html:script xmlns:html=\"http://www.w3.org/1999/xhtml\">alert(1)</html:script>", "<html:script>")]
    [InlineData("<foreignObject><body xmlns=\"http://www.w3.org/1999/xhtml\"><p>x</p></body></foreignObject>", "foreignobject")]
    [InlineData("<rect onload=\"alert(1)\"/>", "'onload'")]
    [InlineData("<rect svg:onclick=\"alert(1)\"/>", "'svg:onclick'")]
    [InlineData("<a xlink:href=\"javascript:alert(1)\"><text>x</text></a>", "javascript:")]
    [InlineData("<a href=\"jav&#x61;script:alert(1)\"><text>x</text></a>", "javascript:")]
    [InlineData("<a href=\"data:text/html,x\"><text>x</text></a>", "data:")]
    [InlineData("<a><set attributeName=\"href\" to=\"javascript:alert(1)\"/></a>", "animation")]
    [InlineData("<a><animate attributeName=\"href\" values=\"https://example.com/;javascript:alert(1)\"/></a>", "animation")]
    public void Script_in_an_svg_file_is_found(string inside, string found)
    {
        var problem = MarkupSafety.Svg($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\">{inside}</svg>");

        Assert.NotNull(problem);
        Assert.Contains(found, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<!DOCTYPE svg [<!ENTITY x \"&#60;script&#62;alert(1)&#60;/script&#62;\">]><svg>&x;</svg>", "entity")]
    [InlineData("<?xml-stylesheet type=\"text/xsl\" href=\"x.xsl\"?><svg/>", "xml-stylesheet")]
    [InlineData("<svg><rect width=\"10\"", "unfinished")]
    [InlineData("<!DOCTYPE svg [<!ATTLIST rect onload CDATA \"alert(1)\">]><svg><rect/></svg>", "attribute list")]
    public void Entities_stylesheets_and_unfinished_tags_are_refused_in_an_svg_file(string svg, string found) =>
        Assert.Contains(found, MarkupSafety.Svg(svg), StringComparison.Ordinal);

    [Fact]
    public void An_svg_file_is_read_in_the_encoding_a_browser_reads_it_in()
    {
        const string script = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>";

        Assert.Null(MarkupSafety.SvgFile(System.Text.Encoding.UTF8.GetBytes(Drawing)));
        Assert.NotNull(MarkupSafety.SvgFile(System.Text.Encoding.UTF8.GetBytes(script)));
        // UTF-16 with its byte order mark is read as such; without one it can't be told from bytes with NULs in them.
        Assert.Contains("<script>", MarkupSafety.SvgFile([.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(script)]));
        Assert.Contains("NUL", MarkupSafety.SvgFile(System.Text.Encoding.Unicode.GetBytes(script)));
        // A single-byte encoding it declares; anything else that isn't UTF-8 can't be checked.
        Assert.Null(MarkupSafety.SvgFile(System.Text.Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><svg><text>Café</text></svg>")));
        Assert.Contains("encoding", MarkupSafety.SvgFile(System.Text.Encoding.Latin1.GetBytes("<svg><text>Café</text></svg>")));
        Assert.Contains("encoding", MarkupSafety.SvgFile(System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-7\"?><svg/>")));
    }

    [Theory]
    [InlineData(".html")]
    [InlineData(".HTM")]
    [InlineData(".xhtml")]
    [InlineData(".xht")]
    [InlineData(".js")]
    [InlineData(".mjs")]
    [InlineData(".xml")]
    [InlineData(".svgz")]
    public void A_file_a_browser_runs_script_in_isnt_uploaded_for_an_editor(string extension)
    {
        var refused = Assert.Throws<AgentException>(() => OptiCli.Cms.Operations.UploadOperation.RequireNoScript(extension, [1, 2, 3]));
        Assert.Contains("can run script", refused.Message);
        Assert.Contains("CMS edit UI", refused.Hint);
    }

    [Theory]
    [InlineData(".jpg", "<html><script>alert(1)</script></html>")]
    [InlineData(".PNG", "  \n<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData(".gif", "\uFEFF<p>x</p>")]
    public void Markup_named_as_an_image_isnt_uploaded_for_an_editor(string extension, string text)
    {
        var refused = Assert.Throws<AgentException>(() => OptiCli.Cms.Operations.UploadOperation.RequireNoScript(extension, System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Contains("starts like markup", refused.Message);
        Assert.Throws<AgentException>(() => OptiCli.Cms.Operations.UploadOperation.RequireNoScript(".jpg", [0xFF, 0xFE, (byte)'<', 0]));
        OptiCli.Cms.Operations.UploadOperation.RequireNoScript(".jpg", [0xFF, 0xD8, 0xFF, 0xE0]);
        OptiCli.Cms.Operations.UploadOperation.RequireNoScript(".png", [0x89, (byte)'P', (byte)'N', (byte)'G']);
        OptiCli.Cms.Operations.UploadOperation.RequireNoScript(".pdf", System.Text.Encoding.UTF8.GetBytes("<not checked>"));
    }

    [Fact]
    public void An_svg_upload_is_read_and_one_without_script_passes()
    {
        OptiCli.Cms.Operations.UploadOperation.RequireNoScript(".svg", System.Text.Encoding.UTF8.GetBytes(Drawing));
        OptiCli.Cms.Operations.UploadOperation.RequireNoScript(".svg", null);
        OptiCli.Cms.Operations.UploadOperation.RequireNoScript(".png", [0x89, 0x50]);
        var refused = Assert.Throws<AgentException>(() => OptiCli.Cms.Operations.UploadOperation.RequireNoScript(".svg",
            System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>")));
        Assert.Contains("'onload'", refused.Message);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("HTTP://example.com/")]
    [InlineData("mailto:editor@example.com")]
    [InlineData("tel:+4712345678")]
    [InlineData("/en/about-us/")]
    [InlineData("about-us/team")]
    [InlineData("//cdn.example.com/a.png")]
    [InlineData("~/link/0f8fad5bd9cb469fa165708ab64ffc25.aspx")]
    [InlineData("#contact")]
    [InlineData("?page=2")]
    [InlineData("page.aspx?a=1&b=2")]
    public void Web_mail_phone_and_relative_links_pass(string url) => Assert.Null(MarkupSafety.Link(url));

    [Theory]
    [InlineData("javascript:alert(1)", "javascript:")]
    [InlineData("javascript://%0aalert(1)", "javascript:")]
    [InlineData(" JAVASCRIPT:alert(1)", "javascript:")]
    [InlineData("java\nscript:alert(1)", "U+000A")]
    [InlineData("jav&#x61;script:alert(1)", "javascript:")]
    [InlineData("vbscript:msgbox(1)", "vbscript:")]
    [InlineData("data:text/html,<script>alert(1)</script>", "data:")]
    [InlineData("ftp://files.example.com/a.zip", "ftp:")]
    [InlineData("file:///c:/windows", "file:")]
    [InlineData("java&nosuchref;script:alert(1)", "can't be read")]
    [InlineData("javascrip\u0001t:alert(1)", "U+0001")]
    [InlineData("javascript\u0001:alert(1)", "U+0001")]
    [InlineData("/en/\u0000x", "U+0000")]
    [InlineData("javascript\u000B:alert(1)", "U+000B")]
    [InlineData("javascript\u000C:alert(1)", "U+000C")]
    [InlineData("javascript\u001F:alert(1)", "U+001F")]
    [InlineData("https://example.com/\u007F", "U+007F")]
    [InlineData("https://example.com/\n", "U+000A")]
    [InlineData("javascrip&#1;t:alert(1)", "U+0001")]
    public void Other_schemes_are_refused_for_links(string url, string found)
    {
        var problem = MarkupSafety.Link(url);

        Assert.NotNull(problem);
        Assert.Contains(found, problem, StringComparison.Ordinal);
    }
}
