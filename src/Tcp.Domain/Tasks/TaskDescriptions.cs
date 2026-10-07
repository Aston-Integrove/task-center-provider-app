using System.Text.Json.Nodes;

namespace Tcp.Domain.Tasks;

public sealed record TaskDescription(string LanguageCode, string ContentType, string Body);

/// <summary>
/// Picks the description for <c>GET /tasks/{urn}/description</c> from the <c>Accept-Language</c> preferences:
/// exact tag, then same primary language (de matches de-DE), then the provider default language, then any.
/// </summary>
public static class TaskDescriptionSelector
{
    public static IReadOnlyList<TaskDescription> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonArray array) return [];
        return array.OfType<JsonObject>()
            .Select(o => new TaskDescription(
                o["languageCode"]?.GetValue<string>() ?? string.Empty,
                o["contentType"]?.GetValue<string>() ?? "text/html",
                o["body"]?.GetValue<string>() ?? string.Empty))
            .Where(d => d.LanguageCode.Length > 0).ToList();
    }

    public static TaskDescription? Select(IReadOnlyList<TaskDescription> descriptions, IEnumerable<string> preferredTags, string defaultLanguage)
    {
        if (descriptions.Count == 0) return null;

        foreach (var tag in preferredTags)
        {
            if (tag is "*" or "") continue;
            var exact = descriptions.FirstOrDefault(d => Same(d.LanguageCode, tag));
            if (exact is not null) return exact;
            var primary = Primary(tag);
            var sameLanguage = descriptions.FirstOrDefault(d => Same(Primary(d.LanguageCode), primary));
            if (sameLanguage is not null) return sameLanguage;
        }

        return descriptions.FirstOrDefault(d => Same(d.LanguageCode, defaultLanguage)) ?? descriptions[0];
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Primary(string tag) => tag.Split('-', '_')[0];
}
