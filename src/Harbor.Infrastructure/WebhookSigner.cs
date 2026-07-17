using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Harbor.Infrastructure;

/// <summary>
/// HMAC-SHA256 signing for webhook payloads.
///
/// The signature covers "{timestamp}.{body}" rather than the body alone, and
/// the timestamp travels in the header. Signing the body by itself would let
/// anyone who captured one request replay it forever; binding the timestamp
/// into the signed string means a receiver can reject anything older than its
/// tolerance and the attacker cannot re-stamp it without the secret.
/// </summary>
public static class WebhookSigner
{
    public const string SignatureHeader = "X-Harbor-Signature";
    public const string EventHeader = "X-Harbor-Event";
    public const string DeliveryHeader = "X-Harbor-Delivery";

    /// <summary>Creates a new random signing secret ("whsec_" + 48 hex chars).</summary>
    public static string GenerateSecret() =>
        $"whsec_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))}";

    /// <summary>The exact bytes covered by the signature.</summary>
    public static string SignedPayload(long timestamp, string body) =>
        $"{timestamp.ToString(CultureInfo.InvariantCulture)}.{body}";

    /// <summary>Hex-encoded HMAC-SHA256 of "{timestamp}.{body}" under the secret.</summary>
    public static string ComputeSignature(string secret, long timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(SignedPayload(timestamp, body))));

    /// <summary>The full header value, Stripe-style: "t=&lt;unix&gt;,v1=&lt;hex&gt;".</summary>
    public static string SignatureHeaderValue(string secret, DateTimeOffset sentAt, string body)
    {
        var timestamp = sentAt.ToUnixTimeSeconds();
        return $"t={timestamp},v1={ComputeSignature(secret, timestamp, body)}";
    }

    /// <summary>
    /// Verifies a header produced by <see cref="SignatureHeaderValue"/>.
    /// Provided so receivers (and our tests) share one definition of correct.
    /// Comparison is fixed-time to avoid leaking the expected signature.
    /// </summary>
    public static bool TryVerify(
        string secret, string headerValue, string body, DateTimeOffset now, TimeSpan tolerance)
    {
        var parts = headerValue.Split(',');
        var timestampPart = parts.FirstOrDefault(p => p.StartsWith("t=", StringComparison.Ordinal));
        var signaturePart = parts.FirstOrDefault(p => p.StartsWith("v1=", StringComparison.Ordinal));
        if (timestampPart is null || signaturePart is null)
        {
            return false;
        }

        if (!long.TryParse(
                timestampPart[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
        {
            return false;
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (now - sentAt > tolerance || sentAt - now > tolerance)
        {
            return false;
        }

        var expected = ComputeSignature(secret, timestamp, body);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signaturePart[3..]));
    }
}
