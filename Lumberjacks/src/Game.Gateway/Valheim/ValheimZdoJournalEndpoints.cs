namespace Game.Gateway.Valheim;

public static class ValheimZdoJournalEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/valheim/zdo-journal");

        group.MapPost("/mutations", (
            ValheimZdoJournalObject mutation,
            ValheimZdoJournalService journal) =>
        {
            var error = ValidateMutation(mutation);
            if (error is not null) return Results.BadRequest(new { error });
            var result = journal.Record(mutation);
            if (!result.Accepted)
                return Results.Conflict(new
                {
                    error = result.Result,
                    durable_objects = result.DurableObjects,
                });
            return Results.Ok(new
            {
                ok = true,
                result = result.Result,
                recipient_count = result.RecipientCount,
                durable_objects = result.DurableObjects,
            });
        });

        group.MapPost("/interest", (
            ValheimZdoJournalInterest interest,
            HttpContext context,
            ValheimZdoJournalService journal) =>
        {
            var scope = Scope(context, interest.RecipientId);
            if (scope.Error is not null)
                return Results.Json(new { error = scope.Error },
                    statusCode: StatusCodes.Status403Forbidden);
            var error = ValidateInterest(interest);
            if (error is not null) return Results.BadRequest(new { error });
            var result = journal.RegisterInterest(scope.Resolved!, interest);
            return Results.Ok(new
            {
                ok = true,
                recipient_id = result.RecipientId,
                snapshot_count = result.SnapshotCount,
                pending = result.PendingCount,
            });
        }).RequireRateLimiting("consumer");

        group.MapGet("/pending/{worldEpoch}", (
            string worldEpoch,
            string? recipient_id,
            int? limit,
            HttpContext context,
            ValheimZdoJournalService journal) =>
        {
            var scope = Scope(context, recipient_id);
            if (scope.Error is not null)
                return Results.Json(new { error = scope.Error },
                    statusCode: StatusCodes.Status403Forbidden);
            if (!SafeToken(worldEpoch, 96))
                return Results.BadRequest(new { error = "world_epoch_invalid" });
            var deliveries = journal.Pending(scope.Resolved!, worldEpoch, limit ?? 64);
            return Results.Ok(new
            {
                schema_version = 1,
                world_epoch = worldEpoch,
                recipient_id = scope.Resolved,
                deliveries,
            });
        }).RequireRateLimiting("consumer");

        group.MapPost("/ack", (
            ValheimZdoJournalAck ack,
            HttpContext context,
            ValheimZdoJournalService journal) =>
        {
            var scope = Scope(context, ack.RecipientId);
            if (scope.Error is not null)
                return Results.Json(new { error = scope.Error },
                    statusCode: StatusCodes.Status403Forbidden);
            if (!SafeToken(ack.WorldEpoch, 96))
                return Results.BadRequest(new { error = "world_epoch_invalid" });
            if (ack.Sequences is null || ack.Sequences.Length is < 1 or > 1024 ||
                ack.Sequences.Any(value => value <= 0))
                return Results.BadRequest(new { error = "sequences_invalid" });
            var result = journal.Acknowledge(scope.Resolved!, ack.WorldEpoch, ack.Sequences);
            return Results.Ok(new
            {
                ok = true,
                acknowledged = result.Acknowledged,
                unknown = result.Unknown,
            });
        }).RequireRateLimiting("consumer");

        group.MapGet("/status", (ValheimZdoJournalService journal) =>
            Results.Ok(journal.Status()));

        group.MapGet("/status/{runId}/{worldEpoch}", (
            string runId,
            string worldEpoch,
            ValheimZdoJournalService journal) =>
        {
            if (!SafeToken(runId, 80)) return Results.BadRequest(new { error = "run_id_invalid" });
            if (!SafeToken(worldEpoch, 96))
                return Results.BadRequest(new { error = "world_epoch_invalid" });
            return Results.Ok(journal.RunStatus(runId, worldEpoch));
        });

        group.MapPost("/reset/{worldEpoch}", (
            string worldEpoch,
            ValheimZdoJournalService journal) =>
        {
            if (!SafeToken(worldEpoch, 96))
                return Results.BadRequest(new { error = "world_epoch_invalid" });
            return Results.Ok(new
            {
                ok = true,
                world_epoch = worldEpoch,
                objects_removed = journal.Reset(worldEpoch),
            });
        });
    }

    internal static string? ValidateMutation(ValheimZdoJournalObject value)
    {
        if (!SafeToken(value.RunId, 80)) return "run_id_invalid";
        if (!SafeToken(value.WorldEpoch, 96)) return "world_epoch_invalid";
        if (value.ZoneEpoch <= 0) return "zone_epoch_invalid";
        if (value.SourceSequence <= 0) return "source_seq_invalid";
        if (value.ObjectRevision <= 0) return "object_revision_invalid";
        if (value.UidUser == 0 || value.UidId is < 0 or > uint.MaxValue) return "zdo_id_invalid";
        if (value.OwnerRevision is < 0 or > ushort.MaxValue) return "owner_rev_invalid";
        if (value.DataRevision is < 0 or > uint.MaxValue) return "data_rev_invalid";
        if (!float.IsFinite(value.PositionX) || !float.IsFinite(value.PositionY) ||
            !float.IsFinite(value.PositionZ)) return "position_invalid";
        if (!value.Tombstone && string.IsNullOrWhiteSpace(value.BodyBase64))
            return "body_b64_required";
        if (value.BodyBase64.Length > 16 * 1024 * 1024) return "body_b64_too_large";
        if (!string.IsNullOrEmpty(value.DeliveryRecipientId) &&
            !SafeToken(value.DeliveryRecipientId, 96))
            return "delivery_recipient_id_invalid";
        return null;
    }

    internal static string? ValidateInterest(ValheimZdoJournalInterest value)
    {
        if (!SafeToken(value.RecipientId, 96)) return "recipient_id_invalid";
        if (!SafeToken(value.RunId, 80)) return "run_id_invalid";
        if (!SafeToken(value.WorldEpoch, 96)) return "world_epoch_invalid";
        if (value.ZoneEpoch <= 0) return "zone_epoch_invalid";
        if (value.RadiusZones is < 0 or > 32) return "radius_zones_invalid";
        return null;
    }

    static (string? Resolved, string? Error) Scope(
        HttpContext context, string? requestedRecipient)
    {
        var principal = ValheimPrincipal.From(context);
        // The AM4 integration lane reaches the local Gateway over authenticated private-plane
        // tunnels, so there is no enrollment identity to distinguish its two physical clients.
        // Private-plane callers may name a bounded recipient explicitly. Enrolled public callers
        // remain self-scoped by their verified enrollment and cannot select another recipient.
        if (string.Equals(principal?.Kind, "private-plane", StringComparison.Ordinal))
            return SafeToken(requestedRecipient, 96)
                ? (requestedRecipient!.Trim(), null)
                : (null, "private-plane recipient_id is required");
        // The journal is born recipient-scoped. Unlike the migration queue, it has no shared
        // `legacy` rollout toggle that can collapse two enrolled clients into one identity.
        return ValheimRecipientScopePolicy.Resolve(
            principal?.Kind, principal?.Enrollment?.RecipientId, null,
            producerEmitsRecipients: true);
    }

    static bool SafeToken(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
