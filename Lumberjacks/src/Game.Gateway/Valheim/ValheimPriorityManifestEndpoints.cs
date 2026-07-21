using Game.Contracts.Valheim;
using Game.Gateway.WebSocket;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Game.Gateway.Valheim;

public static class ValheimPriorityManifestEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/valheim/priority-manifests");

        group.MapGet("/{manifestId}/delivery-plan", async (
            string manifestId,
            int? reliableBudget,
            int? datagramBudget,
            int? eventLimit,
            ValheimPriorityManifestService manifests,
            CancellationToken cancellationToken) =>
        {
            var result = await manifests.LoadDeliveryPlanAsync(
                manifestId,
                reliableBudget ?? 256,
                datagramBudget ?? 768,
                eventLimit ?? 500,
                cancellationToken);

            if (result.MatchedEventCount == 0)
            {
                return Results.NotFound(new
                {
                    manifest_id = manifestId,
                    message = "No valheim.priority_manifest.objects events matched the manifest id.",
                    eventlog_url = result.EventLogUrl,
                    source_event_count = result.SourceEventCount,
                });
            }

            return Results.Ok(ToResponse(result));
        });

        group.MapPost("/{manifestId}/activate", async (
            string manifestId,
            int? reliableBudget,
            int? datagramBudget,
            int? eventLimit,
            ValheimPriorityManifestService manifests,
            CancellationToken cancellationToken) =>
        {
            var activation = await manifests.ActivateDeliveryPlanAsync(
                manifestId,
                reliableBudget ?? 256,
                datagramBudget ?? 768,
                eventLimit ?? 500,
                cancellationToken);

            if (activation.MatchedEventCount == 0)
            {
                return Results.NotFound(new
                {
                    manifest_id = manifestId,
                    message = "No valheim.priority_manifest.objects events matched the manifest id.",
                    source_event_count = activation.SourceEventCount,
                });
            }

            return Results.Ok(ToActivationResponse(activation));
        });

        group.MapGet("/active", (ValheimPriorityManifestService manifests) =>
        {
            return Results.Ok(new
            {
                active = manifests.GetActivePlans().Select(ToActivationResponse),
            });
        });

        group.MapPost("/{manifestId}/broadcast", async (
            string manifestId,
            string? regionId,
            int? reliableBudget,
            int? datagramBudget,
            int? eventLimit,
            ValheimPriorityManifestService manifests,
            SessionManager sessions,
            UdpTransport udpTransport,
            CancellationToken cancellationToken) =>
        {
            var activation = await manifests.ActivateDeliveryPlanAsync(
                manifestId,
                reliableBudget ?? 256,
                datagramBudget ?? 768,
                eventLimit ?? 500,
                cancellationToken);

            if (activation.MatchedEventCount == 0)
            {
                return Results.NotFound(new
                {
                    manifest_id = manifestId,
                    message = "No valheim.priority_manifest.objects events matched the manifest id.",
                    source_event_count = activation.SourceEventCount,
                });
            }

            var targets = string.IsNullOrWhiteSpace(regionId)
                ? sessions.GetAll()
                : sessions.GetByRegion(regionId);
            var sent = await BroadcastPriorityManifestAsync(activation, targets, cancellationToken);
            var datagramResult = SendDatagramObjects(activation, targets, udpTransport);

            return Results.Ok(new
            {
                manifest_id = activation.ManifestId,
                region_id = regionId,
                target_sessions = targets.Count,
                sent_sessions = sent,
                datagram_objects_sent = datagramResult.Sent,
                datagram_sessions_without_udp = datagramResult.SessionsWithoutUdp,
                activation = ToActivationResponse(activation),
            });
        });
    }

    private static object ToResponse(ValheimPriorityManifestResult result) => new
    {
        manifest_id = result.ManifestId,
        eventlog_url = result.EventLogUrl,
        source_event_count = result.SourceEventCount,
        matched_event_count = result.MatchedEventCount,
        total_input_objects = result.Plan.TotalInputObjects,
        unique_objects = result.Plan.UniqueObjects,
        duplicates_removed = result.Plan.DuplicatesRemoved,
        reliable_budget = result.Plan.ReliableBudget,
        datagram_budget = result.Plan.DatagramBudget,
        reliable = result.Plan.Reliable.Select(ToResponseItem),
        datagram = result.Plan.Datagram.Select(ToResponseItem),
        deferred = result.Plan.Deferred.Select(ToResponseItem),
    };

    private static object ToActivationResponse(ValheimPriorityManifestActivation activation) => new
    {
        manifest_id = activation.ManifestId,
        activated_at = activation.ActivatedAt,
        source_event_count = activation.SourceEventCount,
        matched_event_count = activation.MatchedEventCount,
        total_input_objects = activation.Plan.TotalInputObjects,
        unique_objects = activation.Plan.UniqueObjects,
        duplicates_removed = activation.Plan.DuplicatesRemoved,
        reliable_count = activation.Plan.Reliable.Count,
        datagram_count = activation.Plan.Datagram.Count,
        deferred_count = activation.Plan.Deferred.Count,
        reliable_budget = activation.Plan.ReliableBudget,
        datagram_budget = activation.Plan.DatagramBudget,
    };

    private static object ToResponseItem(ValheimPriorityDeliveryItem item) => new
    {
        stable_key = item.StableKey,
        object_name = item.ObjectName,
        object_kind = item.ObjectKind,
        priority_tier = item.PriorityTier,
        priority_rank = item.PriorityRank,
        priority_order = item.PriorityOrder,
        distance_meters = item.DistanceMeters,
        lane = item.Lane.ToString().ToLowerInvariant(),
        delivery_order = item.DeliveryOrder,
        route_stop_id = item.RouteStopId,
        sample_id = item.SampleId,
        position = new
        {
            x = item.Position.X,
            y = item.Position.Y,
            z = item.Position.Z,
        },
    };

    private static async Task<int> BroadcastPriorityManifestAsync(
        ValheimPriorityManifestActivation activation,
        IReadOnlyCollection<GameSession> targets,
        CancellationToken cancellationToken)
    {
        var envelope = EnvelopeFactory.Create(MessageType.PriorityManifest, new
        {
            manifest_id = activation.ManifestId,
            activated_at = activation.ActivatedAt,
            total_input_objects = activation.Plan.TotalInputObjects,
            unique_objects = activation.Plan.UniqueObjects,
            reliable_count = activation.Plan.Reliable.Count,
            datagram_count = activation.Plan.Datagram.Count,
            deferred_count = activation.Plan.Deferred.Count,
            reliable = activation.Plan.Reliable.Select(ToResponseItem),
            datagram_manifest = activation.Plan.Datagram.Select(ToManifestItem),
            deferred_count_by_tier = activation.Plan.Deferred
                .GroupBy(i => i.PriorityTier)
                .OrderBy(g => g.Key)
                .Select(g => new { priority_tier = g.Key, count = g.Count() }),
        });

        var bytes = Encoding.UTF8.GetBytes(EnvelopeFactory.Serialize(envelope));
        var sent = 0;

        foreach (var session in targets)
        {
            if (session.Socket.State != WebSocketState.Open)
                continue;

            await session.Socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                true,
                cancellationToken);
            sent++;
        }

        return sent;
    }

    private static (int Sent, int SessionsWithoutUdp) SendDatagramObjects(
        ValheimPriorityManifestActivation activation,
        IReadOnlyCollection<GameSession> targets,
        UdpTransport udpTransport)
    {
        var sent = 0;
        var sessionsWithoutUdp = 0;

        foreach (var session in targets)
        {
            if (session.UdpEndpoint == null)
            {
                sessionsWithoutUdp++;
                continue;
            }

            foreach (var item in activation.Plan.Datagram)
            {
                var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(ToResponseItem(item), JsonOptions.Default);

                Span<byte> frame = new byte[BinaryEnvelope.HeaderBytes + payloadBytes.Length];
                var frameLen = BinaryEnvelope.Write(
                    frame,
                    version: 1,
                    MessageTypeId.PriorityManifestObject,
                    DeliveryLane.Datagram,
                    seq: 0,
                    payloadBytes);

                if (udpTransport.TrySend(session, frame[..frameLen]))
                    sent++;
            }
        }

        return (sent, sessionsWithoutUdp);
    }

    private static object ToManifestItem(ValheimPriorityDeliveryItem item) => new
    {
        stable_key = item.StableKey,
        priority_tier = item.PriorityTier,
        priority_rank = item.PriorityRank,
        delivery_order = item.DeliveryOrder,
        route_stop_id = item.RouteStopId,
        sample_id = item.SampleId,
    };
}
