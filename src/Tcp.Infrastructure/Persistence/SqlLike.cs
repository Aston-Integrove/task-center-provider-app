namespace Tcp.Infrastructure.Persistence;

/// <summary>Helpers for <c>EF.Functions.Like(column, pattern, "\")</c>.</summary>
public static class SqlLike
{
    public const string EscapeCharacter = "\\";

    /// <summary>Escapes LIKE wildcards so user input is matched literally (use with ESCAPE '\').</summary>
    public static string Escape(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
         .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);

    public static string Contains(string s) => "%" + Escape(s) + "%";
}
