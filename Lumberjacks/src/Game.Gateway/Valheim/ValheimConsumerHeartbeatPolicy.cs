namespace Game.Gateway.Valheim;

/// <summary>
/// Who a consumer heartbeat gets filed under, decided as a pure function so it can be tested.
///
/// This logic used to live inside the <c>/consumer</c> minimal-API lambda, where nothing could
/// reach it: every test in Game.Gateway.Tests is service-level, there is no WebApplicationFactory
/// harness, and building one means standing up Postgres — which is why the endpoint's recipient
/// override shipped uncovered in the first place. The rule is a function of two values, so it does
/// not need a web server to be checked; it only needed to stop living in one.
/// </summary>
public static class ValheimConsumerHeartbeatPolicy
{
    /// <summary>The heartbeat to record, or the error to return. Exactly one is non-null.</summary>
    public sealed record Result(ValheimZdoConsumerHeartbeat? Recorded, string? Error);

    /// <summary>
    /// <paramref name="recipientId"/> is the server-derived recipient for the caller's credential,
    /// or null when the caller presented no enrollment (the legacy shared-key path).
    ///
    /// The rule, in order: a heartbeat must identify its window, build and time; a caller with an
    /// enrollment is filed under the recipient the SERVER derived, whatever it called itself; a
    /// caller without one has no server-side identity, so its own label is the only one available
    /// and is required.
    /// </summary>
    public static Result Resolve(ValheimZdoConsumerHeartbeat heartbeat, string? recipientId)
    {
        if (string.IsNullOrWhiteSpace(heartbeat.WindowId) ||
            string.IsNullOrWhiteSpace(heartbeat.ModVersion) ||
            string.IsNullOrWhiteSpace(heartbeat.TimestampUtc))
        {
            return new Result(null, "window_id, mod_version, and timestamp_utc are required");
        }

        if (string.IsNullOrWhiteSpace(recipientId))
        {
            // No enrollment to derive from. The caller's own label is all there is, so it is
            // required here even though it is optional above — and it is trusted, because there is
            // nothing better to trust. This path is the legacy shared key, not a volunteer.
            return string.IsNullOrWhiteSpace(heartbeat.ConsumerId)
                ? new Result(null,
                    "consumer_id is required when the caller presents no enrollment to derive a recipient from")
                : new Result(heartbeat, null);
        }

        // The whole point: whatever the caller called itself is discarded. consumer_id is optional
        // as of the stage-3 mod cut (the mod no longer names itself at all) and merely ignored when
        // a frozen 0.5.31 mod still sends the GUID it invented. Overridden rather than refused so a
        // rollback to that mod keeps working; it never reads this response body, so it cannot tell.
        return new Result(heartbeat with { ConsumerId = recipientId }, null);
    }
}
