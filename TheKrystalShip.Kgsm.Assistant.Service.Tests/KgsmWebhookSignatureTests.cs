using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Service.Security;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

public class KgsmWebhookSignatureTests
{
    private const string Secret = "kgsm-webhook-secret";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"EventType\":\"instance_started\"}");

    // Mirrors kgsm: sha256=<base64(HMAC-SHA256(body, secret))>
    private static string Sign(byte[] body, string secret) =>
        "sha256=" + Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void ValidSignature_Verifies()
    {
        KgsmWebhookSignature.Verify(Sign(Body, Secret), Body, Secret).Should().BeTrue();
    }

    [Fact]
    public void WrongSecret_DoesNotVerify()
    {
        KgsmWebhookSignature.Verify(Sign(Body, "other-secret"), Body, Secret).Should().BeFalse();
    }

    [Fact]
    public void TamperedBody_DoesNotVerify()
    {
        var header = Sign(Body, Secret);
        var tampered = Encoding.UTF8.GetBytes("{\"EventType\":\"instance_stopped\"}");
        KgsmWebhookSignature.Verify(header, tampered, Secret).Should().BeFalse();
    }

    [Fact]
    public void MissingPrefix_DoesNotVerify()
    {
        var raw = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body));
        KgsmWebhookSignature.Verify(raw, Body, Secret).Should().BeFalse();
    }

    [Fact]
    public void MissingHeader_DoesNotVerify()
    {
        KgsmWebhookSignature.Verify(null, Body, Secret).Should().BeFalse();
        KgsmWebhookSignature.Verify("", Body, Secret).Should().BeFalse();
    }

    [Fact]
    public void EmptySecret_DoesNotVerify()
    {
        KgsmWebhookSignature.Verify(Sign(Body, Secret), Body, "").Should().BeFalse();
    }
}
