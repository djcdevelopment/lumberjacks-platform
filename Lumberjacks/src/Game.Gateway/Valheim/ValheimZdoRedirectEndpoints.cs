namespace Game.Gateway.Valheim;

/// <summary>
/// Receive endpoint for Valheim ZDO payloads redirected by the Harmony mod
/// after it suppresses the original send. Exposes gate-math counters for the
/// test gate: receipt count == suppressed-send count, with sequence-gap loss
/// detection.
/// </summary>
public static class ValheimZdoRedirectEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/valheim/zdo-redirect");

        group.MapPost("/receipts", (
            ValheimZdoRedirectRequest request,
            HttpContext context,
            ValheimZdoRedirectService redirects,
            SteamEnrollmentService enrollments,
            IConfiguration configuration,
            ILoggerFactory loggerFactory) =>
        {
            if (string.IsNullOrWhiteSpace(request.WindowId))
                return Results.BadRequest(new { error = "window_id is required" });

            var admission = ValheimZdoRedirectAdmissionPolicy.Evaluate(
                request.SchemaVersion, request.ModRelease, ValheimReleaseIdentity.ExpectedModRelease);
            var callerIdentity = ValheimPrincipal.From(context)?.Kind ?? "unknown";
            var logger = loggerFactory.CreateLogger("ComfyLumberjacksIntegration");
            if (!admission.Allowed)
            {
                logger.LogWarning(
                    "ZDO submission rejected caller_identity={CallerIdentity} mod_release={ModRelease} expected_release={ExpectedRelease} error={Error}",
                    callerIdentity, request.ModRelease, admission.AdmittedRelease, admission.Error);
                return Results.Json(new
                {
                    error = admission.Error,
                    expected_mod_release = admission.AdmittedRelease,
                    caller_identity = callerIdentity,
                }, statusCode: admission.StatusCode);
            }

            var schema = request.SchemaVersion.GetValueOrDefault(1);
            var envelopes = schema == ValheimZdoRedirectAdmissionPolicy.CurrentSchemaVersion
                ? request.Payload
                : request.Envelopes;
            if (envelopes is null)
                return Results.BadRequest(new { error = schema == 1 ? "envelopes is required" : "payload is required" });

            if (schema == ValheimZdoRedirectAdmissionPolicy.CurrentSchemaVersion)
            {
                if (string.IsNullOrWhiteSpace(request.SourceInstance))
                    return Results.BadRequest(new { error = "source_instance is required" });
                if (!string.Equals(request.Operation, ValheimZdoRedirectAdmissionPolicy.Operation,
                        StringComparison.Ordinal))
                    return Results.BadRequest(new { error = "operation must be zdo_redirect" });
            }

            var idempotencyKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < envelopes.Count; i++)
            {
                var envelope = envelopes[i];
                if (envelope.Seq is null)
                    return Results.BadRequest(new { error = $"envelope at index {i} is missing seq" });
                if (schema != ValheimZdoRedirectAdmissionPolicy.CurrentSchemaVersion) continue;
                if (string.IsNullOrWhiteSpace(envelope.CorrelationId))
                    return Results.BadRequest(new { error = $"payload at index {i} is missing correlation_id" });
                if (string.IsNullOrWhiteSpace(envelope.CreatedUtc) ||
                    !DateTimeOffset.TryParse(envelope.CreatedUtc, out _))
                    return Results.BadRequest(new { error = $"payload at index {i} has invalid created_utc" });
                if (string.IsNullOrWhiteSpace(envelope.Recipient))
                    return Results.BadRequest(new { error = $"payload at index {i} is missing recipient" });
                if (string.IsNullOrWhiteSpace(envelope.ImportanceClass))
                    return Results.BadRequest(new { error = $"payload at index {i} is missing importance_class" });
                if (string.IsNullOrWhiteSpace(envelope.IdempotencyKey))
                    return Results.BadRequest(new { error = $"payload at index {i} is missing idempotency_key" });
                if (!idempotencyKeys.Add(envelope.IdempotencyKey))
                    return Results.BadRequest(new { error = $"payload at index {i} repeats idempotency_key" });
                if (string.IsNullOrWhiteSpace(envelope.BodyB64))
                    return Results.BadRequest(new { error = $"payload at index {i} is missing body_b64" });
            }

            var source = schema == ValheimZdoRedirectAdmissionPolicy.CurrentSchemaVersion
                ? request.SourceInstance!
                : string.IsNullOrWhiteSpace(request.Source) ? "unknown" : request.Source;
            // The producer can only know a Steam identity: the server derives the destination
            // peer's SteamID64 from the socket at Valheim's per-peer sync-list boundary. It cannot
            // know this Gateway's opaque recipient ids, so the translation belongs here — the same
            // stance ValheimRecipientScopePolicy takes for consumers, which resolves identity from
            // the principal rather than trusting a caller label.
            //
            // Both sides must move together or delivery dies silently. While
            // ValheimQueue:ProducerEmitsRecipients is false the consumer is pinned to `legacy`
            // (ValheimRecipientScopePolicy:22-23), so ingest is pinned to `legacy` too. Splitting
            // them is the F1 failure: an enrolled consumer polling its own empty partition forever
            // with no error, no reject, and no way to notice.
            //
            // Unmapped stays `legacy` deliberately. A SteamID with no ACTIVE enrollment — never
            // enrolled, or revoked — is exactly the frozen-producer case, and `legacy` is where a
            // consumer can still reach it. Returning the raw SteamID here would invent a partition
            // nothing polls.
            var producerEmitsRecipients =
                configuration.GetValue("ValheimQueue:ProducerEmitsRecipients", false);
            var result = producerEmitsRecipients
                ? redirects.RecordEnvelopes(request.WindowId, source, envelopes,
                    envelope => enrollments.GetRecipientId(envelope.Recipient) ?? ValheimRecipient.Legacy)
                : redirects.RecordEnvelopes(request.WindowId, source, envelopes,
                    _ => ValheimRecipient.Legacy);
            var correlations = envelopes.Select(envelope => envelope.CorrelationId)
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            var recipients = envelopes.Select(envelope => envelope.Recipient ?? envelope.RecipientId ?? ValheimRecipient.Legacy)
                .Distinct(StringComparer.Ordinal).ToArray();
            logger.LogInformation(
                "ZDO submission accepted caller_identity={CallerIdentity} mod_release={ModRelease} window_id={WindowId} recipients={Recipients} correlations={Correlations}",
                callerIdentity, request.ModRelease, request.WindowId,
                string.Join(",", recipients), string.Join(",", correlations));

            return Results.Ok(new
            {
                ok = true,
                schema_version = schema,
                window_id = request.WindowId,
                received = result.Received,
                total = result.Total,
                caller_identity = callerIdentity,
                admitted_mod_release = admission.AdmittedRelease,
                recipients,
                correlations,
                legacy_unadmitted = admission.LegacyUnadmitted,
            });
        });

        group.MapGet("/status", (ValheimZdoRedirectService redirects) =>
        {
            return Results.Ok(new
            {
                durable_queue = redirects.PersistenceEnabled,
                persistence_healthy = redirects.PersistenceHealthy,
                wal_bytes = redirects.WalBytes,
                windows = redirects.GetAllStatuses().Select(ToResponse),
            });
        });

        group.MapGet("/status/{windowId}", (string windowId, ValheimZdoRedirectService redirects) =>
        {
            var status = redirects.GetStatus(windowId);
            return Results.Ok(ToResponse(status));
        });

        group.MapPost("/compact", (ValheimZdoRedirectService redirects) =>
        {
            var before = redirects.WalBytes;
            var started = System.Diagnostics.Stopwatch.StartNew();
            var after = redirects.Compact();
            started.Stop();
            return Results.Ok(new
            {
                ok = true,
                before_bytes = before,
                after_bytes = after,
                reduction_bytes = before - after,
                reduction_percent = before == 0 ? 0 : 100d * (before - after) / before,
                duration_ms = started.Elapsed.TotalMilliseconds,
            });
        });

        // A consumer poll is the seat gate's sign of life for this window — see
        // ValheimWindowActivityService. Recorded on the request, not inside the ZDO service, so the
        // hot path is untouched.
        group.MapGet("/pending/{windowId}", (string windowId, int? limit, HttpContext context,
            IConfiguration configuration,
            ValheimZdoRedirectService redirects, ValheimWindowActivityService activity) =>
        {
            var scope = Scope(context, configuration);
            if (scope.Error is not null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            activity.Touch(windowId, scope.Resolved!, DateTime.UtcNow);
            return Results.Ok(new { schema_version = 1, window_id = windowId,
                recipient_id = scope.Resolved, envelopes = redirects.Pending(windowId, scope.Resolved!, limit ?? 64) });
        }).RequireRateLimiting("consumer");

        // Who a consumer IS is the server's to say, not the caller's. Where the caller presented an
        // enrollment, the server-derived RecipientId is what gets recorded — the client cannot
        // select which recipient it is filed as.
        //
        // `consumer_id` is therefore OPTIONAL as of the stage-3 mod cut, which stops the client
        // naming itself at all (ZdoAuthoritativeConsumerRunner no longer has a _consumerId). It
        // stays *accepted* rather than refused because the frozen 0.5.31 mod always sends a GUID it
        // invented, so a mismatch is the normal case and not an attack; refusing it would break
        // every heartbeat from a rolled-back mod. The mod never reads the value back — it ignores
        // this response body entirely — so substituting is invisible to it.
        //
        // Required only when there is no enrollment to derive from: the legacy shared-key path has
        // no server-side identity, so a caller-supplied label is the only one available.
        //
        // Tightening this to a 403 on mismatch is deliberately NOT done here. It becomes correct
        // once no supported mod sends the field, and doing it while a rollback to 0.5.31 must keep
        // working would trade a real outage for a property the override already provides.
        group.MapPost("/consumer", (ValheimZdoConsumerHeartbeat heartbeat, HttpContext context,
            IConfiguration configuration, ValheimZdoConsumerTelemetryService consumers) =>
        {
            // The rule lives in ValheimConsumerHeartbeatPolicy, not here, and that is the point:
            // nothing in this lambda is reachable from a test (Game.Gateway.Tests is service-level
            // throughout, and a WebApplicationFactory here means standing up Postgres), so a
            // decision left inside it is a decision nobody can check. This method now only plumbs.
            var scope = Scope(context, configuration);
            if (scope.Error is not null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var resolved = ValheimConsumerHeartbeatPolicy.Resolve(heartbeat, scope.Resolved);
            if (resolved.Error is not null)
            {
                return Results.BadRequest(new { error = resolved.Error });
            }

            consumers.Record(resolved.Recorded!);
            return Results.Ok(new { ok = true, received_at = DateTimeOffset.UtcNow });
        }).RequireRateLimiting("telemetry");

        app.MapGet("/api/v0/valheim/zdo-consumers/{windowId}", (string windowId,
            ValheimZdoConsumerTelemetryService consumers) => Results.Ok(consumers.Snapshot(windowId)))
            .RequireCors(Game.ServiceDefaults.PublicTelemetryV0.CorsPolicyName);

        group.MapPost("/ack/{windowId}", (string windowId, long[] sequences, HttpContext context,
            IConfiguration configuration,
            ValheimZdoRedirectService redirects, ValheimWindowActivityService activity) =>
        {
            if (sequences is null || sequences.Length == 0)
                return Results.BadRequest(new { error = "sequences is required" });
            var scope = Scope(context, configuration);
            if (scope.Error is not null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            activity.Touch(windowId, scope.Resolved!, DateTime.UtcNow);
            var result = redirects.Acknowledge(windowId, scope.Resolved!, sequences);
            return Results.Ok(new { window_id = windowId, acknowledged = result.Acknowledged, unknown = result.Unknown });
        }).RequireRateLimiting("consumer");

        group.MapPost("/reset/{windowId}", (string windowId, ValheimZdoRedirectService redirects) =>
        {
            var existed = redirects.Reset(windowId);
            return Results.Ok(new
            {
                ok = true,
                window_id = windowId,
                reset = existed,
            });
        });

        group.MapPost("/reset", (ValheimZdoRedirectService redirects) =>
        {
            var cleared = redirects.ResetAll();
            return Results.Ok(new
            {
                ok = true,
                reset_all = true,
                windows_cleared = cleared,
            });
        });
    }

    private static object ToResponse(ValheimZdoRedirectWindowStatus status) => new
    {
        window_id = status.WindowId,
        recipient_id = status.RecipientId,
        receipts = status.Receipts,
        distinct_seq = status.DistinctSeq,
        acknowledged = status.Acknowledged,
        pending = status.Pending,
        duplicates = status.Duplicates,
        min_seq = status.MinSeq,
        max_seq = status.MaxSeq,
        missing_seq = status.MissingSeq,
        seq_tracking_saturated = status.SeqTrackingSaturated,
        empty_body_count = status.EmptyBodyCount,
        first_utc = status.FirstUtc,
        last_utc = status.LastUtc,
        per_prefab = status.PerPrefab,
        per_source = status.PerSource,
    };

    private static (string? Resolved, string? Error) Scope(HttpContext context, IConfiguration configuration)
    {
        var principal = ValheimPrincipal.From(context);
        return ValheimRecipientScopePolicy.Resolve(principal?.Kind,
            principal?.Enrollment?.RecipientId, null,
            configuration.GetValue("ValheimQueue:ProducerEmitsRecipients", false));
    }
}
