using Game.Simulation.Tick;
using Xunit;

namespace Game.Simulation.Tests;

public class BroadcastDeadlineTests
{
    [Theory]
    [InlineData(0, false)]     // default — off
    [InlineData(-1, false)]    // guard against bad config
    [InlineData(-100, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    public void IsEnabledOnlyForPositiveValues(int deadlineMs, bool expected)
        => Assert.Equal(expected, BroadcastDeadline.IsEnabled(deadlineMs));
}
