namespace Tcp.Domain.Tasks;

public sealed record SelectedText(string LanguageCode, string Text, bool IsDefault);

/// <summary>
/// Applies the <c>languages</c> parameter to a <c>LocalizedText[]</c> (US-004-4.3, digest section 7): return the
/// translations of the requested languages that exist; exactly one entry is <c>isDefault</c> (the provider's
/// default language if present in the result, otherwise the first); if none of the requested languages exists,
/// return the default-language text (or the first available) as the default.
/// </summary>
public static class LocalizedTextSelector
{
    public static IReadOnlyList<SelectedText> Select(
        IReadOnlyList<LocalizedText> texts,
        IReadOnlyList<string> requestedLanguages,
        string defaultLanguage)
    {
        if (texts.Count == 0) return [];

        var picked = new List<LocalizedText>();
        foreach (var language in requestedLanguages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var match = texts.FirstOrDefault(t => string.Equals(t.LanguageCode, language, StringComparison.OrdinalIgnoreCase));
            if (match is not null) picked.Add(match);
        }

        if (picked.Count == 0)
        {
            var fallback = texts.FirstOrDefault(t => string.Equals(t.LanguageCode, defaultLanguage, StringComparison.OrdinalIgnoreCase))
                           ?? texts[0];
            return [new SelectedText(fallback.LanguageCode, fallback.Text, true)];
        }

        var defaultIndex = picked.FindIndex(t => string.Equals(t.LanguageCode, defaultLanguage, StringComparison.OrdinalIgnoreCase));
        if (defaultIndex < 0) defaultIndex = 0;
        return picked.Select((t, i) => new SelectedText(t.LanguageCode, t.Text, i == defaultIndex)).ToList();
    }
}
