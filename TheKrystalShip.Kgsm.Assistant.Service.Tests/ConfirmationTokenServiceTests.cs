using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

public class ConfirmationTokenServiceTests
{
    private static ConfirmationTokenService Create(string key = "super-secret-signing-key", int ttl = 300) =>
        new(Options.Create(new AssistantServiceOptions
        {
            Confirmation = new ConfirmationOptions { Key = key, TtlSeconds = ttl }
        }));

    [Fact]
    public void RoundTrip_Uninstall_PreservesFieldsAndUser()
    {
        var service = Create();
        var token = service.Create(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), "user-1");

        service.TryValidate(token, out var parsed, out var userId).Should().BeTrue();
        parsed.Kind.Should().Be(ConfirmationKind.Uninstall);
        parsed.Target.Should().Be("terraria");
        parsed.InstanceName.Should().BeNull();
        userId.Should().Be("user-1");
    }

    [Theory]
    [InlineData(ConfirmationKind.Start)]
    [InlineData(ConfirmationKind.Stop)]
    [InlineData(ConfirmationKind.Restart)]
    [InlineData(ConfirmationKind.Update)]
    [InlineData(ConfirmationKind.Backup)]
    public void RoundTrip_GeneralisedCommand_PreservesKindAndTarget(ConfirmationKind kind)
    {
        // §3.5: the formerly-inline commands now ride the same stateless token. The token
        // carries (int)Kind, so each new member must round-trip (and pass Enum.IsDefined).
        var service = Create();
        var token = service.Create(new PendingConfirmation(kind, "minecraft"), "user-1");

        service.TryValidate(token, out var parsed, out var userId).Should().BeTrue();
        parsed.Kind.Should().Be(kind);
        parsed.Target.Should().Be("minecraft");
        parsed.InstanceName.Should().BeNull();
        userId.Should().Be("user-1");
    }

    [Fact]
    public void RoundTrip_Install_PreservesInstanceName()
    {
        var service = Create();
        var token = service.Create(new PendingConfirmation(ConfirmationKind.Install, "valheim", "myserver"), "user-1");

        service.TryValidate(token, out var parsed, out _).Should().BeTrue();
        parsed.Kind.Should().Be(ConfirmationKind.Install);
        parsed.Target.Should().Be("valheim");
        parsed.InstanceName.Should().Be("myserver");
    }

    [Fact]
    public void RoundTrip_SetConfig_PreservesKeyAndValue()
    {
        // The point of the Ck/Cv payload fields: a config edit must survive the token
        // round-trip, including a value with spaces and '=' (the executable_arguments case).
        var service = Create();
        var token = service.Create(
            new PendingConfirmation(ConfirmationKind.SetConfig, "minecraft",
                InstanceName: null, ConfigKey: "executable_arguments", ConfigValue: "--foo=bar baz"),
            "user-1");

        service.TryValidate(token, out var parsed, out var userId).Should().BeTrue();
        parsed.Kind.Should().Be(ConfirmationKind.SetConfig);
        parsed.Target.Should().Be("minecraft");
        parsed.ConfigKey.Should().Be("executable_arguments");
        parsed.ConfigValue.Should().Be("--foo=bar baz");
        userId.Should().Be("user-1");
    }

    [Fact]
    public void RoundTrip_SetConfig_EmptyValue_IsPreserved()
    {
        var service = Create();
        var token = service.Create(
            new PendingConfirmation(ConfirmationKind.SetConfig, "minecraft",
                InstanceName: null, ConfigKey: "executable_arguments", ConfigValue: ""),
            "user-1");

        service.TryValidate(token, out var parsed, out _).Should().BeTrue();
        parsed.ConfigKey.Should().Be("executable_arguments");
        parsed.ConfigValue.Should().Be("");
    }

    [Fact]
    public void TamperedPayload_FailsValidation()
    {
        var service = Create();
        var token = service.Create(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), "user-1");

        // Flip a character in the payload segment (before the '.').
        var dot = token.IndexOf('.');
        var tampered = (token[0] == 'A' ? 'B' : 'A') + token[1..dot] + token[dot..];

        service.TryValidate(tampered, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TokenSignedWithDifferentKey_FailsValidation()
    {
        var token = Create("key-one").Create(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), "user-1");
        Create("key-two").TryValidate(token, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredToken_FailsValidation()
    {
        var service = Create(ttl: 1);
        var token = service.Create(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), "user-1");

        // Tokens expire on whole-second (unix time) granularity, so wait well past the
        // 1s TTL to cross at least two second boundaries regardless of start offset.
        await Task.Delay(2500);

        service.TryValidate(token, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Garbage_FailsValidation()
    {
        var service = Create();
        service.TryValidate("not.a.token", out _, out _).Should().BeFalse();
        service.TryValidate("no-dot", out _, out _).Should().BeFalse();
        service.TryValidate("", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void WithoutKey_IsNotConfigured_AndCannotIssueOrValidate()
    {
        var service = Create(key: "");
        service.IsConfigured.Should().BeFalse();
        service.TryValidate("anything", out _, out _).Should().BeFalse();
        var act = () => service.Create(new PendingConfirmation(ConfirmationKind.Uninstall, "x"), "user-1");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TokenIsBoundToTheStagingUser()
    {
        // The token is authentic and parses, but it surfaces the staging user so the
        // /confirm endpoint can reject a different principal presenting it.
        var service = Create();
        var token = service.Create(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), "alice");

        service.TryValidate(token, out _, out var userId).Should().BeTrue();
        userId.Should().Be("alice");
        userId.Should().NotBe("bob");
    }
}
