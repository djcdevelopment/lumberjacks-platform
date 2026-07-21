using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimZdoInjectionServiceTests
{
    private const string Window = "i4-test";
    private const string Client = "omen";

    [Fact]
    public void StagePollAckIsIdempotent()
    {
        var service = new ValheimZdoInjectionService();
        var command = ValidCommand();

        Assert.True(service.Stage(Window, command).Ok);
        var duplicate = service.Stage(Window, command);
        Assert.True(duplicate.Ok);
        Assert.True(duplicate.Duplicate);
        Assert.Single(service.Poll(Window, Client).Commands);

        Assert.True(service.Ack(new ValheimZdoInjectionAckRequest
        {
            WindowId = Window,
            CommandId = command.CommandId,
            ClientId = Client,
            Applied = true,
            Rendered = true,
            ObservedOwner = command.Owner,
        }).Ok);
        Assert.Empty(service.Poll(Window, Client).Commands);

        var status = service.GetStatus(Window);
        Assert.Equal(1, status.Commands);
        Assert.Equal(2, status.Polls);
        Assert.True(status.Acks[$"{command.CommandId}@{Client}"].Rendered);
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void MalformedCommandsFailClosed(ValheimZdoInjectionCommand command, string expected)
    {
        var result = new ValheimZdoInjectionService().Stage(Window, command);
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> InvalidCommands()
    {
        yield return [ValidCommand() with { Action = "delete" }, "action"];
        yield return [ValidCommand() with { Prefab = "../../Wood" }, "prefab"];
        yield return [ValidCommand() with { Owner = 99 }, "uid_user"];
        yield return [ValidCommand() with { UidId = (long)uint.MaxValue + 1 }, "uid_id"];
        yield return [ValidCommand() with { OwnerRev = 0 }, "owner_rev"];
        yield return [ValidCommand() with { DataRev = (long)uint.MaxValue + 1 }, "data_rev"];
        yield return [ValidCommand() with { Pos = [double.NaN, 0, 0] }, "finite"];
        yield return [ValidCommand() with { Pos = [20_001, 0, 0] }, "outside"];
    }

    private static ValheimZdoInjectionCommand ValidCommand() => new()
    {
        CommandId = "synthetic-wood-1",
        Action = "upsert",
        Prefab = "Wood",
        UidUser = 5_497_853_135_698,
        UidId = 1,
        Owner = 5_497_853_135_698,
        OwnerRev = 1,
        DataRev = 1,
        Pos = [9376, 105, 544],
    };
}
