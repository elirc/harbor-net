using Harbor.Infrastructure;

namespace Harbor.Tests.Unit;

public class WebhookSignerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private const string Secret = "whsec_testsecret";
    private const string Body = """{"event":"conversation.created"}""";
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void GenerateSecret_IsPrefixedAndRandom()
    {
        var first = WebhookSigner.GenerateSecret();
        var second = WebhookSigner.GenerateSecret();

        Assert.StartsWith("whsec_", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SignatureHeader_CarriesTimestampAndSignature()
    {
        var header = WebhookSigner.SignatureHeaderValue(Secret, Now, Body);

        Assert.StartsWith($"t={Now.ToUnixTimeSeconds()},v1=", header);
    }

    [Fact]
    public void SignedPayload_BindsTimestampToBody()
    {
        Assert.Equal($"{Now.ToUnixTimeSeconds()}.{Body}",
            WebhookSigner.SignedPayload(Now.ToUnixTimeSeconds(), Body));
    }

    [Fact]
    public void Signature_IsDeterministic()
    {
        Assert.Equal(
            WebhookSigner.SignatureHeaderValue(Secret, Now, Body),
            WebhookSigner.SignatureHeaderValue(Secret, Now, Body));
    }

    [Fact]
    public void Verify_AcceptsAFreshSignature()
    {
        var header = WebhookSigner.SignatureHeaderValue(Secret, Now, Body);

        Assert.True(WebhookSigner.TryVerify(Secret, header, Body, Now, Tolerance));
    }

    [Fact]
    public void Verify_RejectsATamperedBody()
    {
        var header = WebhookSigner.SignatureHeaderValue(Secret, Now, Body);

        Assert.False(WebhookSigner.TryVerify(
            Secret, header, """{"event":"conversation.closed"}""", Now, Tolerance));
    }

    [Fact]
    public void Verify_RejectsTheWrongSecret()
    {
        var header = WebhookSigner.SignatureHeaderValue(Secret, Now, Body);

        Assert.False(WebhookSigner.TryVerify("whsec_other", header, Body, Now, Tolerance));
    }

    [Fact]
    public void Verify_RejectsAReplayOutsideTolerance()
    {
        var header = WebhookSigner.SignatureHeaderValue(Secret, Now, Body);

        // The signature is still cryptographically valid; the timestamp is not.
        Assert.False(WebhookSigner.TryVerify(
            Secret, header, Body, Now.AddMinutes(10), Tolerance));
        Assert.True(WebhookSigner.TryVerify(
            Secret, header, Body, Now.AddMinutes(4), Tolerance));
    }

    [Fact]
    public void Verify_RejectsATimestampFromTheFuture()
    {
        var header = WebhookSigner.SignatureHeaderValue(Secret, Now.AddMinutes(10), Body);

        Assert.False(WebhookSigner.TryVerify(Secret, header, Body, Now, Tolerance));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=def")]
    [InlineData("v1=abc")]
    [InlineData("t=123")]
    public void Verify_RejectsMalformedHeaders(string header)
    {
        Assert.False(WebhookSigner.TryVerify(Secret, header, Body, Now, Tolerance));
    }

    [Fact]
    public void Signature_ChangesWithTheTimestamp()
    {
        var first = WebhookSigner.ComputeSignature(Secret, Now.ToUnixTimeSeconds(), Body);
        var second = WebhookSigner.ComputeSignature(Secret, Now.AddSeconds(1).ToUnixTimeSeconds(), Body);

        Assert.NotEqual(first, second);
    }
}
