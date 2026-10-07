using System.Globalization;

namespace Tcp.Domain.Identity;

/// <summary>
/// The IAS Global User ID is the only user key sent to Task Center (constitution IV).
/// It is always stored and compared as a lower-case, hyphenated GUID.
/// </summary>
public static class GlobalUserId
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Guid.TryParse(value.Trim(), out var guid) || guid == Guid.Empty) return false;
        normalized = guid.ToString("D", CultureInfo.InvariantCulture);
        return true;
    }

    public static string? Normalize(string? value) => TryNormalize(value, out var n) ? n : null;
}
