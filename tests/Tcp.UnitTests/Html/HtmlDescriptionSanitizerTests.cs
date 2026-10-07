using Tcp.Infrastructure.Html;

namespace Tcp.UnitTests.Html;

public class HtmlDescriptionSanitizerTests
{
    private static readonly HtmlDescriptionSanitizer Sanitizer = new();

    [Theory] // T005-02: dangerous constructs are stripped
    [InlineData("<script>alert(1)</script>Hello", "Hello")]
    [InlineData("<p onclick=\"alert(1)\">Hi</p>", "<p>Hi</p>")]
    [InlineData("<img src=x onerror=alert(1)>Text", "Text")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>Safe", "Safe")]
    [InlineData("<object data=\"x\"></object><embed src=\"x\">Safe", "Safe")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>", "<a>click</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">click</a>", "<a>click</a>")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>", "<a>x</a>")]
    [InlineData("<form action=\"/steal\"><input name=\"x\"></form>Hi", "Hi")]
    [InlineData("<style>body{display:none}</style>Visible", "Visible")]
    [InlineData("<svg onload=alert(1)></svg>Hi", "Hi")]
    [InlineData("<p style=\"background:url(javascript:alert(1))\">x</p>", "<p>x</p>")]
    [InlineData("<b data-x=\"1\" id=\"a\" class=\"c\">bold</b>", "<b>bold</b>")]
    public void Strips_dangerous_markup(string input, string expected) =>
        Sanitizer.Sanitize(input).Should().Be(expected);

    [Theory] // T005-02: allowed constructs are kept
    [InlineData("<p>Please approve <b>PR 4711</b></p>")]
    [InlineData("<ul><li>One</li><li>Two</li></ul>")]
    [InlineData("<ol><li>First</li></ol>")]
    [InlineData("<h1>T</h1><h2>T</h2><h3>T</h3><h4>T</h4>")]
    [InlineData("<strong>s</strong><em>e</em><i>i</i>")]
    [InlineData("Line<br>break")]
    [InlineData("<table><thead><tr><th>Item</th></tr></thead><tbody><tr><td>Laptop</td></tr></tbody></table>")]
    public void Keeps_the_allow_listed_tags(string html)
    {
        var result = Sanitizer.Sanitize(html);
        result.Should().Be(html.Replace("<br>", "<br>"));
    }

    [Fact]
    public void Links_keep_safe_schemes_and_get_rel_noopener()
    {
        Sanitizer.Sanitize("<a href=\"https://example.com/x?y=1\">site</a>").Should().Be("<a href=\"https://example.com/x?y=1\" rel=\"noopener noreferrer\">site</a>");
        Sanitizer.Sanitize("<a href=\"http://example.com\">s</a>").Should().Contain("href=\"http://example.com\"");
        Sanitizer.Sanitize("<a href=\"mailto:a@b.c\">m</a>").Should().Contain("href=\"mailto:a@b.c\"");
        Sanitizer.Sanitize("<a href=\"ftp://example.com\">f</a>").Should().NotContain("href");
    }

    [Fact]
    public void Span_style_keeps_only_harmless_properties_and_only_on_spans()
    {
        var result = Sanitizer.Sanitize("<span style=\"color:red;position:fixed;top:0;font-weight:bold\">x</span>");
        result.Should().Contain("color").And.Contain("font-weight").And.NotContain("position").And.NotContain("top");

        Sanitizer.Sanitize("<p style=\"color:red\">x</p>").Should().Be("<p>x</p>"); // style is for spans only
        Sanitizer.Sanitize("<span style=\"width:expression(alert(1))\">x</span>").Should().NotContain("expression");
        Sanitizer.Sanitize("<td href=\"https://x\">c</td>").Should().NotContain("href"); // href is for links only
    }

    [Fact]
    public void Disallowed_wrappers_keep_their_text_and_children()
    {
        Sanitizer.Sanitize("<div><p>kept</p><section>also kept</section></div>").Should().Be("<p>kept</p>also kept");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_yields_empty_output(string? input) => Sanitizer.Sanitize(input).Should().BeEmpty();

    [Fact]
    public void Text_is_html_encoded_not_interpreted()
    {
        Sanitizer.Sanitize("a &lt;script&gt;alert(1)&lt;/script&gt; b").Should().NotContain("<script");
        Sanitizer.Sanitize("1 < 2 and 3 > 2").Should().NotContain("<script");
    }
}
