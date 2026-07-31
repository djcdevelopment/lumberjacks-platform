using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimOwnershipLeaseServiceTests
{
    [Fact]
    public void ContenderFindsTheTargetAndFailsHolderValidation()
    {
        var service = new ValheimOwnershipLeaseService();
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var lease = service.Issue(
            "c8-run", "world-1", 101, 7, "owner-logical", 1001,
            "shared-action", 20, now);

        var found = service.FindByAction("c8-run", "shared-action");
        Assert.NotNull(found);
        Assert.Equal(lease.UidUser, found!.UidUser);
        Assert.Equal(lease.UidId, found.UidId);

        var validation = service.Validate(
            "c8-run", found.WorldEpoch, found.UidUser, found.UidId,
            "contender-logical", found.Epoch, now.AddSeconds(1));

        Assert.False(validation.Accepted);
        Assert.Equal("holder_mismatch", validation.Reason);
    }

    [Fact]
    public void ActionLookupIsRunScoped()
    {
        var service = new ValheimOwnershipLeaseService();
        var now = DateTimeOffset.UtcNow;
        service.Issue(
            "run-a", "world-1", 101, 7, "owner", 1001,
            "shared-action", 20, now);

        Assert.Null(service.FindByAction("run-b", "shared-action"));
    }
}
