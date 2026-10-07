using System.Globalization;
using System.Text.RegularExpressions;

namespace Tcp.Domain.Tasks;

/// <summary>
/// Custom attribute values are strings that must match the definition type (digest section 6). Task Center
/// silently drops invalid values, so we validate before sending and omit (and log) anything invalid.
/// </summary>
public static partial class CustomAttributeValidator
{
    public const int MaxValueLength = 255;

    public static readonly IReadOnlyList<string> Types = ["STRING", "BOOLEAN", "INTEGER", "FLOAT", "DATE", "DATETIME", "TIME"];

    [GeneratedRegex(@"^\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d\.\d{3}Z$")]
    private static partial Regex DateTimePattern();

    [GeneratedRegex(@"^(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d$")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^-?(?:0|[1-9]\d*)$")]
    private static partial Regex IntegerPattern();

    /// <summary>Returns the normalised value, or false when <paramref name="value"/> is invalid for <paramref name="type"/>.</summary>
    public static bool TryNormalize(string type, string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null) return false;

        switch (type)
        {
            case "STRING":
                normalized = value.Length > MaxValueLength ? value[..MaxValueLength] : value;
                return true;

            case "BOOLEAN":
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) { normalized = "true"; return true; }
                if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) { normalized = "false"; return true; }
                return false;

            case "INTEGER":
                if (!IntegerPattern().IsMatch(value) || !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i))
                    return false;
                normalized = i.ToString(CultureInfo.InvariantCulture);
                return true;

            case "FLOAT":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                    return false;
                normalized = d.ToString("R", CultureInfo.InvariantCulture);
                return normalized.Length <= MaxValueLength;

            case "DATE":
                if (!DatePattern().IsMatch(value) ||
                    !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
                normalized = value;
                return true;

            case "DATETIME":
                if (!DateTimePattern().IsMatch(value) ||
                    !DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _)) return false;
                normalized = value;
                return true;

            case "TIME":
                if (!TimePattern().IsMatch(value)) return false;
                normalized = value;
                return true;

            default:
                return false;
        }
    }

    public static bool IsValid(string type, string? value) => TryNormalize(type, value, out _);
}
