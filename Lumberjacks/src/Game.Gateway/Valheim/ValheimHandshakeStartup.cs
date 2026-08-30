using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Game.Gateway.Valheim;

public sealed record ValheimHandshakeStartupSettings(
    string WindowId,
    int SeatCapacity,
    bool StrictRosterEnabled = false,
    bool StrictReleaseEnabled = false);

public static class ValheimHandshakeStartup
{
    public const string DefaultWindowId = "p7-primary-v1";
    public const int DefaultSeatCapacity = 1;
    public const string AlphaSeatGateKey = "LUMBERJACKS_ALPHA_SEAT_GATE";
    public const string StrictRosterKey = "LUMBERJACKS_STRICT_ROSTER_ENABLED";
    public const string StrictReleaseKey = "LUMBERJACKS_STRICT_RELEASE_ENABLED";

    public static ValheimHandshakeStartupSettings FromConfiguration(IConfiguration configuration)
    {
        var windowId = configuration["LUMBERJACKS_AUTHORITATIVE_WINDOW_ID"];
        if (string.IsNullOrWhiteSpace(windowId))
            windowId = DefaultWindowId;

        var seatCapacity = SeatCapacityFromConfiguration(configuration);
        var strictRoster = BooleanFromConfiguration(configuration, StrictRosterKey);
        var strictRelease = BooleanFromConfiguration(configuration, StrictReleaseKey);

        var context = new ValheimHandshakeServerContext
        {
            SeatCapacity = seatCapacity,
            StrictRosterEnabled = strictRoster,
            StrictReleaseEnabled = strictRelease,
        };
        var contextError = ValheimHandshakeService.ValidateContext(context);
        if (contextError is not null)
            throw new InvalidOperationException(
                $"Invalid ValheimHandshake startup configuration: {contextError}");

        return new ValheimHandshakeStartupSettings(windowId, seatCapacity, strictRoster, strictRelease);
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
            StrictReleaseEnabled = settings.StrictReleaseEnabled,
        });

        if (!result.Ok)
            throw new InvalidOperationException(
                $"Invalid ValheimHandshake startup configuration: {result.Error}");

        logger?.LogInformation(
            "Configured Valheim handshake startup window {WindowId} with seat capacity {SeatCapacity}, strict roster {StrictRosterEnabled}, and strict release {StrictReleaseEnabled}",
            settings.WindowId,
            settings.SeatCapacity,
            settings.StrictRosterEnabled,
            settings.StrictReleaseEnabled);
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

    private static bool BooleanFromConfiguration(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        if (bool.TryParse(raw.Trim(), out var enabled))
            return enabled;
        throw new InvalidOperationException(
            $"Invalid ValheimHandshake startup configuration: {key} must be true or false");
    }
}
