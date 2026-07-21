using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimRecipientScopePolicyTests
{
    [Fact]
    public void EnrollmentUsesServerRecipientAndIgnoresRequestedLabel()
    {
        var result = ValheimRecipientScopePolicy.Resolve(
            "enrollment", "rcpt-a", "forged", producerEmitsRecipients: true);
        Assert.Null(result.Error);
        Assert.Equal("rcpt-a", result.Resolved);
    }

    [Fact]
    public void EnrollmentWithoutRecipientFailsClosed()
    {
        var result = ValheimRecipientScopePolicy.Resolve(
            "enrollment", " ", "legacy", producerEmitsRecipients: true);
        Assert.Null(result.Resolved);
        Assert.Contains("recipient_id", result.Error);
    }

    [Theory]
    [InlineData("private-plane")]
    [InlineData("shared-client-key")]
    public void LegacyPrincipalsUseNamedLegacyBucket(string principalKind)
    {
        var result = ValheimRecipientScopePolicy.Resolve(
            principalKind, null, "another", producerEmitsRecipients: true);
        Assert.Null(result.Error);
        Assert.Equal(ValheimRecipient.Legacy, result.Resolved);
    }

    [Fact]
    public void UnknownPrincipalFailsClosed()
    {
        var result = ValheimRecipientScopePolicy.Resolve(
            "anonymous", null, "a", producerEmitsRecipients: true);
        Assert.Null(result.Resolved);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void LegacyUnscopedConsumer_StillDrainsItsOwnWindow()
    {
        var result = ValheimRecipientScopePolicy.Resolve(
            "shared-client-key", null, "client-b", producerEmitsRecipients: true);
        Assert.Null(result.Error);
        Assert.Equal(ValheimRecipient.Legacy, result.Resolved);
    }
}
