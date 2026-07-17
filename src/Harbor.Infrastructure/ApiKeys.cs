using System.Security.Cryptography;
using System.Text;

namespace Harbor.Infrastructure;

/// <summary>
/// Teammate API keys: generated once, shown once, and stored only as a
/// SHA-256 hex digest.
/// </summary>
public static class ApiKeys
{
    /// <summary>Creates a new random API key ("hbk_" + 48 hex chars).</summary>
    public static string Generate() =>
        $"hbk_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))}";

    /// <summary>Hex-encoded SHA-256 digest of the raw key, as persisted on the teammate.</summary>
    public static string Hash(string apiKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
