using AngleSharp.Dom;
using Ganss.Xss;

namespace Tcp.Infrastructure.Html;

/// <summary>
/// Sanitises task descriptions on write (US-005-1.5, US-004-6.3). Allow-list: p, br, b, strong, i, em, ul, ol, li,
/// table, thead, tbody, tr, th, td, a[href], h1-h4, span[style]. No scripts, event handlers, iframes, forms or
/// <c>javascript:</c> URLs; only a handful of harmless CSS properties survive in <c>span[style]</c>.
/// </summary>
public sealed class HtmlDescriptionSanitizer
{
    private static readonly string[] Tags =
        ["p", "br", "b", "strong", "i", "em", "ul", "ol", "li", "table", "thead", "tbody", "tr", "th", "td", "a", "h1", "h2", "h3", "h4", "span"];

    private static readonly HashSet<string> DropContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "object", "embed", "noscript", "template", "svg", "math", "form", "textarea", "select", "title",
    };

    private static readonly string[] CssProperties =
        ["color", "background-color", "font-weight", "font-style", "text-decoration", "text-align"];

    private readonly HtmlSanitizer _sanitizer;

    public HtmlDescriptionSanitizer()
    {
        _sanitizer = new HtmlSanitizer { KeepChildNodes = true, AllowDataAttributes = false };
        _sanitizer.AllowedTags.Clear();
        foreach (var tag in Tags) _sanitizer.AllowedTags.Add(tag);

        _sanitizer.AllowedAttributes.Clear();
        _sanitizer.AllowedAttributes.Add("href");
        _sanitizer.AllowedAttributes.Add("style");

        _sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" }) _sanitizer.AllowedSchemes.Add(scheme);

        _sanitizer.AllowedCssProperties.Clear();
        foreach (var property in CssProperties) _sanitizer.AllowedCssProperties.Add(property);

        _sanitizer.AllowedClasses.Clear();

        // KeepChildNodes preserves the text of unknown wrappers (div, section...), but for these elements the content is
        // code or non-text payload, so they are removed together with everything inside them.
        _sanitizer.RemovingTag += (_, e) =>
        {
            if (DropContent.Contains(e.Tag.LocalName)) e.Tag.InnerHtml = string.Empty;
        };

        // The library's attribute allow-list is global; narrow it to "href only on a, style only on span".
        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement element) return;
            var name = element.LocalName;
            if (name != "a") element.RemoveAttribute("href");
            if (name != "span") element.RemoveAttribute("style");
            if (name == "a" && element.HasAttribute("href"))
                element.SetAttribute("rel", "noopener noreferrer");
        };
    }

    public string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? string.Empty : _sanitizer.Sanitize(html);
}
