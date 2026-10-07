using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Tcp.Api.Diagnostics;

/// <summary>
/// Masks secrets in headers, query strings and bodies before anything is logged or kept in the
/// request log (constitution VI/VIII). Deny-list based; names compared case-insensitively.
/// </summary>
public static partial class Redactor
{
    public const string Mask = "***";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "X-Functions-Key",
    };

    private static readonly HashSet<string> SensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "client_secret", "assertion", "access_token", "refresh_token", "id_token", "password",
        "secret", "token", "SAMLResponse", "client_assertion",
    };

    public static string RedactHeader(string name, string value) =>
        SensitiveHeaders.Contains(name) ? Mask : value;

    public static bool IsSensitiveField(string name) => SensitiveFields.Contains(name);

    public static string RedactQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return string.Empty;
        var parsed = QueryHelpers.ParseQuery(query);
        var sb = new StringBuilder();
        foreach (var (key, values) in parsed)
            foreach (var v in values)
            {
                sb.Append(sb.Length == 0 ? '?' : '&')
                  .Append(Uri.EscapeDataString(key)).Append('=')
                  .Append(IsSensitiveField(key) ? Mask : Uri.EscapeDataString(v ?? string.Empty));
            }
        return sb.ToString();
    }

    public static string RedactBody(string? body, string? contentType)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;
        var ct = contentType ?? string.Empty;

        if (ct.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return RedactForm(body);

        if (ct.Contains("json", StringComparison.OrdinalIgnoreCase) || LooksLikeJson(body))
            return RedactJson(body);

        return body;
    }

    private static bool LooksLikeJson(string body)
    {
        var t = body.AsSpan().TrimStart();
        return t.Length > 0 && (t[0] == '{' || t[0] == '[');
    }

    private static string RedactForm(string body)
    {
        var parsed = QueryHelpers.ParseQuery(body);
        var sb = new StringBuilder();
        foreach (var (key, values) in parsed)
            foreach (var v in values)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(key)).Append('=')
                  .Append(IsSensitiveField(key) ? Mask : Uri.EscapeDataString(v ?? string.Empty));
            }
        return sb.ToString();
    }

    private static string RedactJson(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is null) return body;
            MaskNode(node);
            return node.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            // Truncated or malformed JSON: fall back to a textual pass so secrets never slip through.
            return JsonPropertyPattern().Replace(body, m =>
                IsSensitiveField(m.Groups["name"].Value) ? $"\"{m.Groups["name"].Value}\":\"{Mask}\"" : m.Value);
        }
    }

    private static void MaskNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IsSensitiveField(key)) obj[key] = Mask;
                    else if (obj[key] is { } child) MaskNode(child);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (item is not null) MaskNode(item);
                break;
        }
    }

    [GeneratedRegex("\"(?<name>[^\"]+)\"\\s*:\\s*\"(?<value>[^\"]*)\"?")]
    private static partial Regex JsonPropertyPattern();
}
