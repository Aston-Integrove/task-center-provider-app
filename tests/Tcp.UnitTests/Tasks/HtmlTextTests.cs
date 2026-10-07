using Tcp.Domain.Tasks;

namespace Tcp.UnitTests.Tasks;

public class HtmlTextTests
{
    [Theory]
    [InlineData("<p>Please approve <b>PR 4711</b></p>", "Please approve PR 4711")]
    [InlineData("Line one<br>Line two<br/>Line three", "Line one\nLine two\nLine three")]
    [InlineData("<p>First</p><p>Second</p>", "First\n\nSecond")]
    [InlineData("<ul><li>One</li><li>Two</li></ul>", "- One\n- Two")]
    [InlineData("<table><tr><th>Item</th><td>Laptop</td></tr><tr><th>Qty</th><td>1</td></tr></table>", "Item Laptop\nQty 1")]
    [InlineData("Fish &amp; chips &lt;3 &quot;quoted&quot;", "Fish & chips <3 \"quoted\"")]
    [InlineData("Caf&eacute; &#8364;5", "Café €5")]
    [InlineData("a&nbsp;&nbsp;b", "a b")]
    [InlineData("<script>alert(1)</script>Safe<style>p{color:red}</style>", "Safe")]
    [InlineData("<!-- hidden -->Visible", "Visible")]
    [InlineData("<h1>Title</h1><div>Body</div>", "Title\nBody")]
    [InlineData("  spaced   out  \n\n\n\n  text  ", "spaced out\n\ntext")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Converts_html_to_plain_text(string? html, string expected) =>
        HtmlText.ToPlainText(html).Should().Be(expected);

    [Fact]
    public void Plain_text_without_markup_is_unchanged() =>
        HtmlText.ToPlainText("Just a simple sentence.").Should().Be("Just a simple sentence.");

    [Fact]
    public void Output_never_contains_angle_bracket_tags()
    {
        var nasty = "<img src=x onerror=alert(1)><a href=\"javascript:alert(1)\">click</a><iframe src=//evil></iframe>";
        HtmlText.ToPlainText(nasty).Should().Be("click");
    }
}
