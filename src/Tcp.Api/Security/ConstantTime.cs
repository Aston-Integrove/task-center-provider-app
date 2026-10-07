using System.Security.Cryptography;
using System.Text;

namespace Tcp.Api.Security;

public static class ConstantTime
{
    /// <summary>Compares via SHA-256 digests so neither length nor content differences leak through timing.</summary>
    public static bool Equals(string? a, string? b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a ?? string.Empty)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b ?? string.Empty)));
}
