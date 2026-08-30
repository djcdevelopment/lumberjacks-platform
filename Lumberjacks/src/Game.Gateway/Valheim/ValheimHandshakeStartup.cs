using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Game.Gateway.Valheim;

public sealed record ValheimHandshakeStartupSettings(
    string WindowId,
    int SeatCapacity,
    bool StrictRosterEnabled = false);

public static class ValheimHandshakeStartup
{
    public const string DefaultWindowId = "p7-primary-v1";
    public const int DefaultSeatCapacity = 1;
    public const string AlphaSeatGateKey = "LUMBERJACKS_ALPHA_SEAT_GATE";
    public const string StrictRosterKey = "LUMBERJACKS_STRICT_ROSTER_ENABLED";

    public static ValheimHandshakeStartupSettings FromConfiguration(IConfiguration configuration)
    {
        var windowId = configuration["LUMBERJACKS_AUTHORITATIVE_WINDOW_ID"];
        if (string.IsNullOrWhiteSpace(windowId))
            windowId = DefaultWindowId;

        var seatCapacity = SeatCapacityFromConfiguration(configuration);
        var strictRoster = StrictRosterFromConfiguration(configuration);

        var context = new ValheimHandshakeServerContext
        {
            SeatCapacity = seatCapacity,
            StrictRosterEnabled = strictRoster,
        };
        var contextError = ValheimHandshakeService.ValidateContext(context);
        if (contextError is not null)
            throw new InvalidOperationException(
                $"Invalid ValheimHandshake startup configuration: {contextError}");

        return new ValheimHandshakeStartupSettings(windowId, seatCapacity, strictRoster);
    }

    public static void Configure(
        ValheimHandshakeService service,
        IConfiguration configuration,
        ILogger? logger = null)
    {
        var settings = FromConfiguration(configuration);
        var result = service.Configure(settings.WindowId, new ValheimHandshakeServerContext
        {
            SeatCapacity = settings.SeatCapacity,
            StrictRosterEnabled = settings.StrictRosterEnabled,
        });

        if (!result.Ok)
            throw new InvalidOperationException(
                $"Invalid ValheimHandshake startup configuration: {result.Error}");

        logger?.LogInformation(
            "Configured Valheim handshake startup window {WindowId} with seat capacity {SeatCapacity} and strict roster {StrictRosterEnabled}",
            settings.WindowId,
            settings.SeatCapacity,
            settings.StrictRosterEnabled);
    }

    private static int SeatCapacityFromConfiguration(IConfiguration configuration)
    {
        var alphaSeatGate = configuration[AlphaSeatGateKey];
        if (!string.IsNullOrWhiteSpace(alphaSeatGate))
        {
            return alphaSeatGate.Trim().ToLowerInvariant() switch
            {
                "disabled" => 0,
                "off" => 0,
                "none" => 0,
                "one-seat" => 1,
                "one_seat" => 1,
                "reserved-one-seat" => 1,
                "reserved_one_seat" => 1,
                "on" => 1,
                _ => throw new InvalidOperationException(
                    $"Invalid ValheimHandshake startup configuration: {AlphaSeatGateKey} must be disabled or one-seat"),
            };
        }

        var seatCapacity = DefaultSeatCapacity;
        var rawSeatCapacity = configuration["ValheimHandshake:SeatCapacity"];
        if (!string.IsNullOrWhiteSpace(rawSeatCapacity)
            && !int.TryParse(rawSeatCapacity, out seatCapacity))
            throw new InvalidOperationException(
                "Invalid ValheimHandshake startup configuration: seat_capacity must be an integer");
        return seatCapacity;
    }

    private static bool StrictRosterFromConfiguration(IConfiguration configuration)
    {
        var raw = configuration[StrictRosterKey];
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        if (bool.TryParse(raw.Trim(), out var enabled))
            return enabled;
        throw new InvalidOperationException(
            $"Invalid ValheimHandshake startup configuration: {StrictRosterKey} must be true or false");
    }
}
