using CommunitySurvival.Networking;
using Xunit;

namespace Game.Contracts.Tests;

public sealed class NativeAccessConfigTests
{
    [Theory]
    [InlineData("127.0.0.1:14000", "ws://127.0.0.1:14000/game")]
    [InlineData("ws://127.0.0.1:4000/", "ws://127.0.0.1:4000/game")]
    [InlineData("wss://play.example.test/game", "wss://play.example.test/game")]
    public void PrivateRAndDNormalizesOnlyTheGameEndpoint(string input, string expected)
    {
        var access = NativeAccessConfig.ForPrivateRAndD(input);

        Assert.True(access.PrivateRAndD);
        Assert.Equal(expected, access.GatewayUrl);
        Assert.Equal(NativeAccessConfig.DefaultRAndDUsername, access.RAndDUsername);
    }

    [Fact]
    public void PrivateRAndDRejectsNonGamePaths()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            NativeAccessConfig.ForPrivateRAndD("ws://127.0.0.1:4000/admin"));

        Assert.Contains("/game", error.Message);
    }
}
