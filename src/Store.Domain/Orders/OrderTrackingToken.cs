using System.Security.Cryptography;
using System.Text;

namespace Store.Domain.Orders;

/// <summary>
/// Unguessable link token for guest order tracking. Order numbers are sequential and meant for
/// talking to staff; the token is what grants read access without an account.
///
/// Only a SHA-256 hash is stored. The raw token is shown once at checkout (and put in the
/// confirmation link), so a database leak does not expose working tracking links.
/// </summary>
public static class OrderTrackingToken
{
    public const int HashLength = 64; // hex SHA-256

    /// <summary>128 random bits, base64url (22 chars): safe in URLs, infeasible to guess.</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static bool LooksValid(string? token) =>
        token is { Length: 22 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
