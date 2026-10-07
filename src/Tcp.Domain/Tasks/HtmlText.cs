using System.Net;
using System.Text.RegularExpressions;

namespace Tcp.Domain.Tasks;

/// <summary>
/// Converts the (sanitised) HTML description used by the provider's own UI into the plain text that
/// <c>GET /tasks/{urn}/description</c> must return according to TaskProviderV2.json ("Returns the plain text task
/// description", <c>text/plain; charset=utf-8</c>). No HTML parser needed: input is our own sanitised markup.
/// </summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptsAndStyles();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"</p\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphEnds();

    [GeneratedRegex(@"</(div|h[1-6]|tr|table|ul|ol|blockquote|section|article|pre)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnds();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEnds();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"\n[ \t]+")]
    private static partial Regex LeadingSpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpaceRuns();

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var text = ScriptsAndStyles().Replace(html, string.Empty);
        text = Comments().Replace(text, string.Empty);
        text = LineBreaks().Replace(text, "\n");
        text = ParagraphEnds().Replace(text, "\n\n");
        text = BlockEnds().Replace(text, "\n");
        text = ListItems().Replace(text, "\n- ");
        text = CellEnds().Replace(text, "\t");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ').Replace("\r\n", "\n", StringComparison.Ordinal);
        text = SpaceRuns().Replace(text.Replace('\t', ' '), " ");
        text = TrailingSpaces().Replace(text, "\n");
        text = LeadingSpaces().Replace(text, "\n");
        text = BlankRuns().Replace(text, "\n\n");
        return text.Trim();
    }
}
