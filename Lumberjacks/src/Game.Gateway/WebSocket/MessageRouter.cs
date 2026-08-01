using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using Game.Gateway.Valheim;
using Game.ServiceDefaults;
using Game.Simulation.Handlers;
using Game.Simulation.Tick;
using Game.Simulation.World;

namespace Game.Gateway.WebSocket;

public class MessageRouter
{
    private readonly SessionManager _sessions;
    private readonly InputQueue _inputQueue;
    private readonly WorldState _world;
    private readonly PlayerHandler _playerHandler;
    private readonly PlaceStructureHandler _placeStructureHandler;
    private readonly InventoryHandler _inventoryHandler;
    private readonly ValheimZdoJournalService _zdoJournal;
    private readonly ValheimOwnershipLeaseService _ownershipLeases;
    private readonly ValheimWorldZoneService _worldZones;
    private readonly ILogger<MessageRouter> _logger;
    private readonly ValheimPlayerLifecycle? _valheimPlayers;

    public MessageRouter(
        SessionManager sessions,
        InputQueue inputQueue,
        WorldState world,
        PlayerHandler playerHandler,
        PlaceStructureHandler placeStructureHandler,
        InventoryHandler inventoryHandler,
        ValheimZdoJournalService zdoJournal,
        ValheimOwnershipLeaseService ownershipLeases,
        ValheimWorldZoneService worldZones,
        ILogger<MessageRouter> logger,
        ValheimPlayerLifecycle? valheimPlayers = null)
    {
        _sessions = sessions;
        _inputQueue = inputQueue;
        _world = world;
        _playerHandler = playerHandler;
        _placeStructureHandler = placeStructureHandler;
        _inventoryHandler = inventoryHandler;
        _zdoJournal = zdoJournal;
        _ownershipLeases = ownershipLeases;
        _worldZones = worldZones;
        _logger = logger;
        _valheimPlayers = valheimPlayers;
    }

    /// <summary>
    /// Builds a compact region profile payload for the client to generate terrain.
    /// Includes altitude grid and trade winds — enough for heightmap rendering.
    /// </summary>
    private object? BuildRegionProfilePayload(string regionId)
    {
        if (!_world.RegionProfiles.TryGetValue(regionId, out var profile))
            return null;

        return new
        {
            grid_width = profile.GridWidth,
            grid_height = profile.GridHeight,
            altitude_grid = profile.AltitudeGrid,
            trade_wind_x = profile.TradeWindX,
            trade_wind_z = profile.TradeWindZ,
        };
    }

    public async Task RouteAsync(GameSession session, Envelope envelope)
    {
        LumberjacksTelemetry.RecordMessage(envelope.Type, "websocket");
        using var activity = LumberjacksTelemetry.StartMessageActivity(envelope.Type, "websocket");

        switch (envelope.Type)
        {
            case MessageType.JoinRegion:
                await HandleJoinRegionAsync(session, envelope);
                break;

            case MessageType.LeaveRegion:
                await HandleLeaveRegionAsync(session);
                break;

            case MessageType.PlayerMove:
                await HandlePlayerMoveAsync(session, envelope);
                break;

            case MessageType.PlayerInput:
                HandlePlayerInput(session, envelope);
                break;

            case MessageType.PlaceStructure:
                await HandlePlaceStructureAsync(session, envelope);
                break;

            case MessageType.Interact:
                await HandleInteractAsync(session, envelope);
                break;

            case MessageType.ReliableAck:
                await HandleReliableAckAsync(session, envelope);
                break;

            case MessageType.ValheimSessionProbe:
                await HandleValheimSessionProbeAsync(session, envelope);
                break;

            case MessageType.ValheimControlResponse:
                await HandleValheimControlResponseAsync(session, envelope);
                break;

            case MessageType.ValheimDirectPulseProbe:
                await HandleValheimDirectPulseProbeAsync(session, envelope);
                break;

            case MessageType.ValheimPeerBind:
                await HandleValheimPeerBindAsync(session, envelope);
                break;

            case MessageType.ValheimLogicalPeerControl:
                await HandleValheimLogicalPeerControlAsync(session, envelope);
                break;

            case MessageType.ValheimRoutedRpcSend:
                await HandleValheimRoutedRpcSendAsync(session, envelope);
                break;

            case MessageType.ValheimZdoMutation:
                await HandleValheimZdoMutationAsync(session, envelope);
                break;

            case MessageType.ValheimZdoInterest:
                await HandleValheimZdoInterestAsync(session, envelope);
                break;

            case MessageType.ValheimZdoAck:
                HandleValheimZdoAck(session, envelope);
                break;

            case MessageType.ValheimOwnershipLeaseRequest:
                await HandleValheimOwnershipLeaseRequestAsync(session, envelope);
                break;

            case MessageType.ValheimOwnershipLeaseIssue:
                await HandleValheimOwnershipLeaseIssueAsync(session, envelope);
                break;

            case MessageType.ValheimOwnershipAction:
                await HandleValheimOwnershipActionAsync(session, envelope);
                break;

            case MessageType.ValheimOwnershipActionResult:
                await HandleValheimOwnershipActionResultAsync(session, envelope);
                break;

            case MessageType.ValheimWorldDescriptorPublish:
                await HandleValheimWorldDescriptorPublishAsync(session, envelope);
                break;

            case MessageType.ValheimWorldDescriptorRequest:
                await HandleValheimWorldDescriptorRequestAsync(session, envelope);
                break;

            case MessageType.ValheimZoneMembershipEnter:
                await HandleValheimZoneMembershipEnterAsync(session, envelope);
                break;

            case MessageType.ValheimZoneSnapshotPublish:
                await HandleValheimZoneSnapshotPublishAsync(session, envelope);
                break;

            case MessageType.ValheimZoneSnapshotAck:
                await HandleValheimZoneSnapshotAckAsync(session, envelope);
                break;

            case MessageType.ValheimZoneMembershipLeave:
                await HandleValheimZoneMembershipLeaveAsync(session, envelope);
                break;

            case MessageType.ValheimMotionResyncPublish:
                await HandleValheimMotionResyncPublishAsync(session, envelope);
                break;

            default:
                _logger.LogDebug("No route for message type {Type}", envelope.Type);
                break;
        }
    }

    async Task HandleReliableAckAsync(GameSession session, Envelope envelope)
    {
        if (!envelope.Payload.TryGetProperty("through_sequence", out var sequenceElement) ||
            !sequenceElement.TryGetInt64(out var sequence) || sequence < 1)
            throw new InvalidDataException("reliable_ack requires a positive through_sequence");
        var removed = session.Reliable.AcknowledgeThrough(sequence);
        _logger.LogInformation(
            "Valheim reliable ACK connection={ConnectionId} epoch={ResumeEpoch} through={Sequence} removed={Removed}",
            session.ConnectionId, session.ResumeEpoch, sequence, removed);
        if (string.Equals(session.ValheimRole, "client", StringComparison.Ordinal) &&
            _zdoJournal.CurrentWorldEpoch(session.ValheimLogicalPeerId) is { } worldEpoch)
            await SendPendingZdoDeliveriesAsync(session, worldEpoch);
    }

    async Task HandleValheimWorldDescriptorPublishAsync(
        GameSession session, Envelope envelope)
    {
        if (!string.Equals(session.ValheimRole, "server", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId))
            throw new InvalidDataException(
                "world descriptor publish requires the logical server session");

        var descriptor = JsonSerializer.Deserialize<ValheimWorldDescriptor>(
            envelope.Payload.GetRawText(), JsonOptions.Default)
            ?? throw new InvalidDataException("world descriptor payload missing");
        ValidateWorldDescriptor(descriptor);
        var key =
            $"world-descriptor:{descriptor.WorldEpoch}:{descriptor.SaveEpoch}:{descriptor.RunId}";
        if (session.Reliable.TryAcceptClientMessage(envelope.Seq, key))
            _worldZones.Publish(descriptor);

        var receipt = await session.SendReliableAsync(
            MessageType.ValheimWorldDescriptorReceipt,
            new
            {
                descriptor.RunId,
                descriptor.WorldEpoch,
                descriptor.SaveEpoch,
                publish_count = _worldZones.PublishCount,
                logical_peer_id = session.ValheimLogicalPeerId,
            },
            CancellationToken.None);
        if (!receipt.Queued) throw new InvalidOperationException(receipt.Reason);
    }

    async Task HandleValheimWorldDescriptorRequestAsync(
        GameSession session, Envelope envelope)
    {
        if (!string.Equals(session.ValheimRole, "client", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId))
            throw new InvalidDataException(
                "world descriptor request requires a logical client session");

        var payload = envelope.Payload;
        var runId = payload.TryGetProperty("run_id", out var runElement)
            ? runElement.GetString() ?? string.Empty
            : string.Empty;
        var faultMode = payload.TryGetProperty("fault_mode", out var faultElement)
            ? faultElement.GetString() ?? string.Empty
            : string.Empty;
        if (!SafeToken(runId, 80) ||
            faultMode is not (
                "" or "wrong_protocol" or "wrong_release" or
                "wrong_world_generation"))
            throw new InvalidDataException("invalid world descriptor request");

        var descriptor = _worldZones.Current();
        if (descriptor is null)
        {
            var status = await session.SendReliableAsync(
                MessageType.ValheimWorldDescriptorStatus,
                new { run_id = runId, available = false, reason = "server_not_published" },
                CancellationToken.None);
            if (!status.Queued) throw new InvalidOperationException(status.Reason);
            return;
        }

        var delivered = faultMode switch
        {
            "wrong_protocol" => descriptor with
            {
                RunId = runId,
                ProtocolVersion = descriptor.ProtocolVersion + 1,
            },
            "wrong_release" => descriptor with
            {
                RunId = runId,
                ReleaseId = "c7-incompatible-release",
            },
            "wrong_world_generation" => descriptor with
            {
                RunId = runId,
                WorldGenerationVersion = descriptor.WorldGenerationVersion + 1,
            },
            _ => descriptor with { RunId = runId },
        };
        var queued = await session.SendReliableAsync(
            MessageType.ValheimWorldDescriptor, delivered, CancellationToken.None);
        if (!queued.Queued) throw new InvalidOperationException(queued.Reason);
    }

    static void ValidateWorldDescriptor(ValheimWorldDescriptor value)
    {
        if (value.SchemaVersion != 1 || value.ProtocolVersion != 1)
            throw new InvalidDataException("world_descriptor_protocol_invalid");
        if (!SafeToken(value.ReleaseId, 96) || !SafeToken(value.RunId, 80) ||
            !SafeToken(value.WorldEpoch, 96))
            throw new InvalidDataException("world_descriptor_identity_invalid");
        if (string.IsNullOrWhiteSpace(value.WorldName) || value.WorldName.Length > 80 ||
            string.IsNullOrWhiteSpace(value.SeedName) || value.SeedName.Length > 128 ||
            value.WorldUid == 0 || value.WorldGenerationVersion <= 0 ||
            value.SaveEpoch <= 0 || double.IsNaN(value.NetworkTime) ||
            double.IsInfinity(value.NetworkTime))
            throw new InvalidDataException("world_descriptor_fields_invalid");
        if (string.IsNullOrWhiteSpace(value.StartLocationName) ||
            value.StartLocationName.Length > 128 ||
            value.GlobalKeys is null || value.GlobalKeys.Length > 512 ||
            value.GlobalKeys.Any(key =>
                string.IsNullOrWhiteSpace(key) || key.Length > 256 ||
                key.Any(char.IsControl)) ||
            value.LocationIcons is null ||
            value.LocationIcons.Length is < 1 or > 4096 ||
            value.LocationIcons.Any(icon =>
                string.IsNullOrWhiteSpace(icon.Name) || icon.Name.Length > 128 ||
                icon.Name.Any(char.IsControl) ||
                !double.IsFinite(icon.X) || !double.IsFinite(icon.Y) ||
                !double.IsFinite(icon.Z)) ||
            !value.LocationIcons.Any(icon =>
                string.Equals(
                    icon.Name, value.StartLocationName,
                    StringComparison.Ordinal)))
            throw new InvalidDataException("world_descriptor_bootstrap_invalid");
    }

    async Task HandleValheimZoneMembershipEnterAsync(
        GameSession session, Envelope envelope)
    {
        RequireValheimRole(session, "client", "zone membership enter");
        var request = JsonSerializer.Deserialize<ValheimZoneMembershipRequest>(
            envelope.Payload.GetRawText(), JsonOptions.Default)
            ?? throw new InvalidDataException("zone membership payload missing");
        ValidateZoneRequest(request);
        var descriptor = _worldZones.Current()
            ?? throw new InvalidDataException("zone membership world descriptor missing");
        if (!string.Equals(request.WorldEpoch, descriptor.WorldEpoch,
                StringComparison.Ordinal) ||
            !string.Equals(request.RunId, descriptor.RunId, StringComparison.Ordinal))
            throw new InvalidDataException("zone membership world scope mismatch");

        var key =
            $"zone-enter:{request.RunId}:{request.ActionId}:{request.ZoneEpoch}:{request.ZoneX}:{request.ZoneY}:{request.Mode}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, key))
            return;
        var state = _worldZones.Begin(session.ValheimLogicalPeerId, request);

        if (request.Mode == "withhold")
        {
            var withheld = await session.SendReliableAsync(
                MessageType.ValheimZoneSnapshotComplete,
                new
                {
                    request.RunId,
                    request.ActionId,
                    request.WorldEpoch,
                    request.ZoneEpoch,
                    state.SnapshotEpoch,
                    request.ZoneX,
                    request.ZoneY,
                    chunk_count = 0,
                    object_count = 0,
                    withheld = true,
                },
                CancellationToken.None);
            if (!withheld.Queued) throw new InvalidOperationException(withheld.Reason);
            return;
        }

        var server = FindValheimSession("server")
            ?? throw new InvalidDataException("zone membership server missing");
        var queued = await server.SendReliableAsync(
            MessageType.ValheimZoneSnapshotBuild,
            new
            {
                recipient_id = session.ValheimLogicalPeerId,
                request.RunId,
                request.ActionId,
                request.WorldEpoch,
                request.ZoneEpoch,
                state.SnapshotEpoch,
                request.ZoneX,
                request.ZoneY,
                request.Mode,
            },
            CancellationToken.None);
        if (!queued.Queued) throw new InvalidOperationException(queued.Reason);
    }

    async Task HandleValheimZoneSnapshotPublishAsync(
        GameSession session, Envelope envelope)
    {
        RequireValheimRole(session, "server", "zone snapshot publish");
        var publication = JsonSerializer.Deserialize<ValheimZoneSnapshotPublication>(
            envelope.Payload.GetRawText(), JsonOptions.Default)
            ?? throw new InvalidDataException("zone snapshot publication missing");
        ValidateZonePublication(publication);
        var key =
            $"zone-publish:{publication.RecipientId}:{publication.SnapshotEpoch}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, key))
            return;
        _worldZones.Publish(publication);
        var target = _sessions.FindByValheimLogicalPeer(publication.RecipientId)
            ?? throw new InvalidDataException("zone snapshot recipient missing");
        await SendNextZoneChunkAsync(target);
    }

    async Task HandleValheimZoneSnapshotAckAsync(
        GameSession session, Envelope envelope)
    {
        RequireValheimRole(session, "client", "zone snapshot ACK");
        var snapshotEpoch = ReadInt64(envelope.Payload, "snapshot_epoch");
        var chunkIndex = ReadInt32(envelope.Payload, "chunk_index");
        if (snapshotEpoch < 1 || chunkIndex < 1)
            throw new InvalidDataException("invalid zone snapshot ACK");
        var key = $"zone-ack:{snapshotEpoch}:{chunkIndex}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, key))
            return;

        var result = _worldZones.Ack(
            session.ValheimLogicalPeerId, snapshotEpoch, chunkIndex);
        if (result.Duplicate) return;
        if (!result.Complete)
        {
            await SendNextZoneChunkAsync(session);
            return;
        }
        if (!_worldZones.TryMarkComplete(
                session.ValheimLogicalPeerId, snapshotEpoch))
            return;
        var state = result.State;
        var complete = await session.SendReliableAsync(
            MessageType.ValheimZoneSnapshotComplete,
            new
            {
                state.Request.RunId,
                state.Request.ActionId,
                state.Request.WorldEpoch,
                state.Request.ZoneEpoch,
                state.SnapshotEpoch,
                state.Request.ZoneX,
                state.Request.ZoneY,
                chunk_count = state.Objects!.Length,
                object_count = state.Objects.Length,
                withheld = false,
            },
            CancellationToken.None);
        if (!complete.Queued) throw new InvalidOperationException(complete.Reason);
    }

    async Task HandleValheimZoneMembershipLeaveAsync(
        GameSession session, Envelope envelope)
    {
        RequireValheimRole(session, "client", "zone membership leave");
        var runId = ReadString(envelope.Payload, "run_id");
        var actionId = ReadString(envelope.Payload, "action_id");
        var snapshotEpoch = ReadInt64(envelope.Payload, "snapshot_epoch");
        if (!SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
            snapshotEpoch < 1)
            throw new InvalidDataException("invalid zone membership leave");
        var key = $"zone-leave:{runId}:{actionId}:{snapshotEpoch}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, key))
            return;

        var state = _worldZones.Leave(
            session.ValheimLogicalPeerId, runId, actionId, snapshotEpoch);
        var objects = state.Objects!.Select(value => new
        {
            uid_user = value.UidUser,
            uid_id = value.UidId,
        }).ToArray();
        var server = FindValheimSession("server")
            ?? throw new InvalidDataException("zone membership server missing");
        var release = await server.SendReliableAsync(
            MessageType.ValheimZoneSnapshotRelease,
            new
            {
                recipient_id = session.ValheimLogicalPeerId,
                state.Request.RunId,
                state.Request.ActionId,
                state.Request.WorldEpoch,
                state.Request.ZoneEpoch,
                state.SnapshotEpoch,
                state.Request.ZoneX,
                state.Request.ZoneY,
                objects,
            },
            CancellationToken.None);
        if (!release.Queued) throw new InvalidOperationException(release.Reason);

        var left = await session.SendReliableAsync(
            MessageType.ValheimZoneMembershipLeft,
            new
            {
                state.Request.RunId,
                state.Request.ActionId,
                state.Request.WorldEpoch,
                state.Request.ZoneEpoch,
                state.SnapshotEpoch,
                state.Request.ZoneX,
                state.Request.ZoneY,
                objects,
            },
            CancellationToken.None);
        if (!left.Queued) throw new InvalidOperationException(left.Reason);
    }

    async Task HandleValheimMotionResyncPublishAsync(
        GameSession session, Envelope envelope)
    {
        RequireValheimRole(session, "client", "motion resync publish");
        if (string.IsNullOrWhiteSpace(session.RegionId))
            throw new InvalidDataException(
                "motion resync publish requires joined region");

        var runId = ReadString(envelope.Payload, "run_id");
        var actionId = ReadString(envelope.Payload, "action_id");
        var sourceMotionSequence = ReadInt32(
            envelope.Payload, "source_motion_sequence");
        var gapStart = ReadInt32(envelope.Payload, "gap_start_sequence");
        var gapEnd = ReadInt32(envelope.Payload, "gap_end_sequence");
        var zdoUser = ReadInt64(envelope.Payload, "zdo_user_id");
        var zdoId = ReadUInt32(envelope.Payload, "zdo_id");
        var x = ReadDouble(envelope.Payload, "x");
        var y = ReadDouble(envelope.Payload, "y");
        var z = ReadDouble(envelope.Payload, "z");
        var velocityX = ReadDouble(envelope.Payload, "velocity_x");
        var velocityY = ReadDouble(envelope.Payload, "velocity_y");
        var velocityZ = ReadDouble(envelope.Payload, "velocity_z");
        var yaw = ReadDouble(envelope.Payload, "yaw");
        var sentMilliseconds = ReadUInt32(
            envelope.Payload, "sent_milliseconds");
        var reason = ReadString(envelope.Payload, "reason");

        if (!SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
            zdoUser == 0 || zdoId == 0 ||
            sourceMotionSequence is < 1 or > ushort.MaxValue ||
            gapStart is < 1 or > ushort.MaxValue ||
            gapEnd is < 1 or > ushort.MaxValue ||
            gapEnd < gapStart ||
            reason is not ("gap_recovery" or "local_teleport") ||
            !FiniteAndBounded(x, 1_000_000) ||
            !FiniteAndBounded(y, 1_000_000) ||
            !FiniteAndBounded(z, 1_000_000) ||
            !FiniteAndBounded(velocityX, 500) ||
            !FiniteAndBounded(velocityY, 500) ||
            !FiniteAndBounded(velocityZ, 500) ||
            !FiniteAndBounded(yaw, 720))
            throw new InvalidDataException("invalid motion resync publication");

        var key =
            $"motion-resync:{runId}:{actionId}:{sourceMotionSequence}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, key))
            return;

        var targets = _sessions.GetByRegion(session.RegionId)
            .Where(candidate =>
                candidate.SessionId != session.SessionId &&
                string.Equals(
                    candidate.ValheimRole, "client",
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(candidate.ValheimLogicalPeerId) &&
                !string.Equals(
                    candidate.ValheimLogicalPeerId,
                    session.ValheimLogicalPeerId,
                    StringComparison.Ordinal))
            .ToArray();
        var targetCount = 0;
        foreach (var target in targets)
        {
            var queued = await target.SendReliableAsync(
                MessageType.ValheimMotionResync,
                new
                {
                    run_id = runId,
                    action_id = actionId,
                    source_logical_peer_id = session.ValheimLogicalPeerId,
                    source_motion_sequence = sourceMotionSequence,
                    gap_start_sequence = gapStart,
                    gap_end_sequence = gapEnd,
                    zdo_user_id = zdoUser,
                    zdo_id = zdoId,
                    x,
                    y,
                    z,
                    velocity_x = velocityX,
                    velocity_y = velocityY,
                    velocity_z = velocityZ,
                    yaw,
                    sent_milliseconds = sentMilliseconds,
                    reason,
                },
                CancellationToken.None);
            if (!queued.Queued)
                throw new InvalidOperationException(queued.Reason);
            targetCount++;
        }

        var receipt = await session.SendReliableAsync(
            MessageType.ValheimMotionResyncReceipt,
            new
            {
                run_id = runId,
                action_id = actionId,
                source_motion_sequence = sourceMotionSequence,
                gap_start_sequence = gapStart,
                gap_end_sequence = gapEnd,
                target_count = targetCount,
                result = targetCount > 0 ? "accepted" : "no_target",
            },
            CancellationToken.None);
        if (!receipt.Queued)
            throw new InvalidOperationException(receipt.Reason);
    }

    async Task SendNextZoneChunkAsync(GameSession target)
    {
        var chunk = _worldZones.NextChunk(target.ValheimLogicalPeerId)
            ?? throw new InvalidDataException("zone snapshot has no pending chunk");
        var queued = await target.SendReliableAsync(
            MessageType.ValheimZoneSnapshotChunk,
            new
            {
                chunk.RunId,
                chunk.ActionId,
                chunk.WorldEpoch,
                chunk.ZoneEpoch,
                chunk.SnapshotEpoch,
                chunk.ZoneX,
                chunk.ZoneY,
                chunk.ChunkIndex,
                chunk.ChunkCount,
                @object = chunk.Object,
            },
            CancellationToken.None);
        if (!queued.Queued) throw new InvalidOperationException(queued.Reason);
    }

    GameSession? FindValheimSession(string role) =>
        _sessions.GetAll().FirstOrDefault(candidate =>
            string.Equals(candidate.ValheimRole, role, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(candidate.ValheimLogicalPeerId));

    static void RequireValheimRole(
        GameSession session, string role, string operation)
    {
        if (!string.Equals(session.ValheimRole, role, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId))
            throw new InvalidDataException(
                $"{operation} requires a logical {role} session");
    }

    static void ValidateZoneRequest(ValheimZoneMembershipRequest value)
    {
        if (!SafeToken(value.RunId, 80) || !SafeToken(value.ActionId, 80) ||
            !SafeToken(value.WorldEpoch, 96) || value.ZoneEpoch < 1 ||
            Math.Abs((long)value.ZoneX) > 1_000_000 ||
            Math.Abs((long)value.ZoneY) > 1_000_000 ||
            value.Mode is not ("resume_once" or "withhold"))
            throw new InvalidDataException("invalid zone membership request");
    }

    static void ValidateZonePublication(ValheimZoneSnapshotPublication value)
    {
        if (!SafeToken(value.RecipientId, 80) ||
            !SafeToken(value.RunId, 80) || !SafeToken(value.ActionId, 80) ||
            !SafeToken(value.WorldEpoch, 96) || value.ZoneEpoch < 1 ||
            value.SnapshotEpoch < 1 || Math.Abs((long)value.ZoneX) > 1_000_000 ||
            Math.Abs((long)value.ZoneY) > 1_000_000 ||
            value.Objects is not { Length: 3 })
            throw new InvalidDataException("invalid zone snapshot publication");
        foreach (var item in value.Objects)
        {
            if (item.UidUser == 0 || item.UidId == 0 || item.Prefab == 0 ||
                item.OwnerRevision < 0 || item.DataRevision < 0 ||
                !float.IsFinite(item.PositionX) ||
                !float.IsFinite(item.PositionY) ||
                !float.IsFinite(item.PositionZ) ||
                string.IsNullOrEmpty(item.BodyBase64) ||
                item.BodyBase64.Length > 1_500_000)
                throw new InvalidDataException(
                    "invalid zone snapshot object"
                    + $" uid={item.UidUser}:{item.UidId}"
                    + $" prefab={item.Prefab}"
                    + $" owner_rev={item.OwnerRevision}"
                    + $" data_rev={item.DataRevision}"
                    + $" position={item.PositionX:R},{item.PositionY:R},{item.PositionZ:R}"
                    + $" body_chars={item.BodyBase64?.Length ?? -1}");
            try
            {
                if (Convert.FromBase64String(item.BodyBase64).Length is < 1 or > 1_000_000)
                    throw new InvalidDataException(
                        "invalid zone snapshot object body size");
            }
            catch (FormatException)
            {
                throw new InvalidDataException(
                    "invalid zone snapshot object body base64");
            }
        }
    }

    async Task HandleValheimSessionProbeAsync(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;
        var probeId = payload.TryGetProperty("probe_id", out var probeElement)
            ? probeElement.GetString() ?? string.Empty
            : string.Empty;
        var runId = payload.TryGetProperty("run_id", out var runElement)
            ? runElement.GetString() ?? string.Empty
            : string.Empty;
        var mode = payload.TryGetProperty("mode", out var modeElement)
            ? modeElement.GetString() ?? string.Empty
            : string.Empty;
        if (!SafeToken(probeId, 192) || !SafeToken(runId, 80) ||
            mode is not ("resume" or "withhold_receipt"))
            throw new InvalidDataException("invalid Valheim session probe");

        if (session.Reliable.TryGetProbe(probeId, out _))
        {
            _logger.LogInformation(
                "Valheim session probe duplicate start ignored connection={ConnectionId} probe={ProbeId}",
                session.ConnectionId, probeId);
            return;
        }
        if (!session.Reliable.CanAddProbe(probeId))
        {
            await SendErrorAsync(session, "RELIABLE_BACKPRESSURE", "session_probe_capacity_reached");
            return;
        }

        var result = await session.SendReliableAsync(
            MessageType.ValheimControlRequest,
            new
            {
                run_id = runId,
                probe_id = probeId,
                mode,
                connection_id = session.ConnectionId,
                issued_resume_epoch = session.ResumeEpoch,
            },
            CancellationToken.None);
        if (!result.Queued)
        {
            await SendErrorAsync(session, "RELIABLE_BACKPRESSURE", result.Reason);
            return;
        }

        if (!session.Reliable.TryAddProbe(new ReliableSessionProbe(
                runId, probeId, mode, result.Sequence, session.ConnectionId)))
            throw new InvalidOperationException("reliable session probe capacity reached");
        _logger.LogInformation(
            "Valheim control request queued connection={ConnectionId} epoch={ResumeEpoch} probe={ProbeId} sequence={Sequence} mode={Mode}",
            session.ConnectionId, session.ResumeEpoch, probeId, result.Sequence, mode);
    }

    async Task HandleValheimControlResponseAsync(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;
        var probeId = payload.TryGetProperty("probe_id", out var probeElement)
            ? probeElement.GetString() ?? string.Empty
            : string.Empty;
        var requestSequence =
            payload.TryGetProperty("request_sequence", out var requestElement) &&
            requestElement.TryGetInt64(out var parsedRequest)
                ? parsedRequest
                : 0;
        var clientSequence =
            payload.TryGetProperty("client_sequence", out var clientElement) &&
            clientElement.TryGetInt64(out var parsedClient)
                ? parsedClient
                : 0;
        if (!session.Reliable.TryGetProbe(probeId, out var probe) ||
            probe.RequestSequence != requestSequence || clientSequence < 1)
            throw new InvalidDataException("Valheim control response does not match a pending request");

        var accepted = session.Reliable.TryAcceptClientMessage(
            clientSequence, probeId + ":" + requestSequence);
        if (accepted) Interlocked.Increment(ref probe.ResponseCount);
        _logger.LogInformation(
            "Valheim control response connection={ConnectionId} epoch={ResumeEpoch} probe={ProbeId} request_sequence={RequestSequence} client_sequence={ClientSequence} accepted={Accepted} response_count={ResponseCount}",
            session.ConnectionId, session.ResumeEpoch, probeId, requestSequence,
            clientSequence, accepted, Volatile.Read(ref probe.ResponseCount));

        if (probe.Mode == "withhold_receipt")
        {
            _logger.LogInformation(
                "Valheim control receipt intentionally withheld connection={ConnectionId} probe={ProbeId}",
                session.ConnectionId, probeId);
            return;
        }

        var receipt = await session.SendReliableAsync(
            MessageType.ValheimControlReceipt,
            new
            {
                run_id = probe.RunId,
                probe_id = probe.ProbeId,
                request_sequence = probe.RequestSequence,
                response_count = Volatile.Read(ref probe.ResponseCount),
                duplicate = !accepted,
                connection_id = session.ConnectionId,
                resume_epoch = session.ResumeEpoch,
            },
            CancellationToken.None);
        if (!receipt.Queued)
            await SendErrorAsync(session, "RELIABLE_BACKPRESSURE", receipt.Reason);
    }

    async Task HandleValheimDirectPulseProbeAsync(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;
        var runId = payload.TryGetProperty("run_id", out var runElement)
            ? runElement.GetString() ?? string.Empty
            : string.Empty;
        var actionId = payload.TryGetProperty("action_id", out var actionElement)
            ? actionElement.GetString() ?? string.Empty
            : string.Empty;
        var mode = payload.TryGetProperty("mode", out var modeElement)
            ? modeElement.GetString() ?? string.Empty
            : string.Empty;
        if (!SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
            mode is not ("deliver" or "withhold"))
            throw new InvalidDataException("invalid Valheim direct pulse probe");

        if (mode == "withhold")
        {
            _logger.LogInformation(
                "Valheim direct pulse intentionally withheld connection={ConnectionId} epoch={ResumeEpoch} run={RunId} action={ActionId}",
                session.ConnectionId, session.ResumeEpoch, runId, actionId);
            return;
        }

        var result = await session.SendReliableAsync(
            MessageType.ValheimDirectPulse,
            new
            {
                run_id = runId,
                action_id = actionId,
                connection_id = session.ConnectionId,
                resume_epoch = session.ResumeEpoch,
                issued_utc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
        if (!result.Queued)
        {
            await SendErrorAsync(session, "RELIABLE_BACKPRESSURE", result.Reason);
            return;
        }
        _logger.LogInformation(
            "Valheim direct pulse queued connection={ConnectionId} epoch={ResumeEpoch} run={RunId} action={ActionId} sequence={Sequence}",
            session.ConnectionId, session.ResumeEpoch, runId, actionId, result.Sequence);
    }

    async Task HandleValheimPeerBindAsync(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;
        var role = payload.TryGetProperty("role", out var roleElement)
            ? roleElement.GetString() ?? string.Empty
            : string.Empty;
        if (!payload.TryGetProperty("peer_uid", out var uidElement) ||
            !uidElement.TryGetInt64(out var peerUid) || peerUid == 0 ||
            !string.Equals(role, session.ValheimRole, StringComparison.Ordinal))
            throw new InvalidDataException("invalid Valheim peer binding");

        var key = $"peer-bind:{role}:{peerUid}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, key))
        {
            if (session.ValheimPeerUid != peerUid)
                throw new InvalidDataException("conflicting Valheim peer binding");
            await AnnounceLogicalCounterpartsAsync(session, role);
            return;
        }

        var existing = _sessions.FindByValheimPeer(peerUid);
        if (existing != null && existing.SessionId != session.SessionId)
            throw new InvalidDataException("Valheim peer UID already bound");
        if (role == "server" && _sessions.GetAll().Any(candidate =>
                candidate.SessionId != session.SessionId &&
                candidate.ValheimRole == "server" &&
                candidate.ValheimPeerUid.HasValue))
            throw new InvalidDataException("Valheim server session already bound");

        session.ValheimPeerUid = peerUid;
        _logger.LogInformation(
            "Valheim peer bound connection={ConnectionId} role={Role} peer={PeerUid}",
            session.ConnectionId, role, peerUid);

        await AnnounceLogicalCounterpartsAsync(session, role);
    }

    async Task AnnounceLogicalCounterpartsAsync(
        GameSession session, string role)
    {
        var counterparts = _sessions.GetAll()
            .Where(candidate =>
                candidate.SessionId != session.SessionId &&
                candidate.ValheimPeerUid.HasValue &&
                !string.IsNullOrWhiteSpace(candidate.ValheimLogicalPeerId) &&
                !string.Equals(candidate.ValheimRole, role, StringComparison.Ordinal))
            .ToArray();
        foreach (var counterpart in counterparts)
        {
            var toCounterpart = await counterpart.SendReliableAsync(
                MessageType.ValheimLogicalPeerAttached,
                LogicalPeerPayload(session),
                CancellationToken.None);
            if (!toCounterpart.Queued)
                throw new InvalidOperationException(toCounterpart.Reason);

            var toSession = await session.SendReliableAsync(
                MessageType.ValheimLogicalPeerAttached,
                LogicalPeerPayload(counterpart),
                CancellationToken.None);
            if (!toSession.Queued)
                throw new InvalidOperationException(toSession.Reason);
        }
    }

    async Task HandleValheimLogicalPeerControlAsync(
        GameSession session, Envelope envelope)
    {
        if (!session.ValheimPeerUid.HasValue ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId))
            throw new InvalidDataException(
                "logical peer control requires a bound Valheim session");

        var payload = envelope.Payload;
        var targetLogicalPeerId = ReadString(payload, "target_logical_peer_id");
        var control = ReadString(payload, "control");
        if (!SafeToken(targetLogicalPeerId, 96) ||
            control is not ("character_id" or "disconnect"))
            throw new InvalidDataException("invalid logical peer control envelope");

        var target = _sessions.FindByValheimLogicalPeer(targetLogicalPeerId);
        if (target is null || !target.ValheimPeerUid.HasValue)
            throw new InvalidDataException("logical peer control target missing");

        long zdoUserId = 0;
        uint zdoId = 0;
        if (control == "character_id" &&
            (!payload.TryGetProperty("zdo_user_id", out var userElement) ||
             !userElement.TryGetInt64(out zdoUserId) || zdoUserId == 0 ||
             !payload.TryGetProperty("zdo_id", out var idElement) ||
             !idElement.TryGetUInt32(out zdoId) || zdoId == 0))
            throw new InvalidDataException("logical character id invalid");

        var key =
            $"logical-control:{session.ValheimLogicalPeerId}:{targetLogicalPeerId}:{control}:{zdoUserId}:{zdoId}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, key))
            return;

        if (control == "character_id")
        {
            var registration = session.AuthorizeValheimCharacter(zdoUserId, zdoId);
            if (_valheimPlayers != null)
                await _valheimPlayers.CharacterRegisteredAsync(session, registration);
        }

        var queued = await target.SendReliableAsync(
            MessageType.ValheimLogicalPeerControl,
            new
            {
                source_logical_peer_id = session.ValheimLogicalPeerId,
                source_peer_uid = session.ValheimPeerUid.Value,
                target_logical_peer_id = targetLogicalPeerId,
                control,
                zdo_user_id = zdoUserId,
                zdo_id = zdoId,
            },
            CancellationToken.None);
        if (!queued.Queued) throw new InvalidOperationException(queued.Reason);
    }

    static object LogicalPeerPayload(GameSession session) => new
    {
        role = session.ValheimRole,
        peer_uid = session.ValheimPeerUid!.Value,
        logical_peer_id = session.ValheimLogicalPeerId,
        character = session.ValheimCharacter,
    };

    async Task HandleValheimRoutedRpcSendAsync(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;
        var runId = ReadString(payload, "run_id");
        var actionId = ReadString(payload, "action_id");
        var routeId = ReadString(payload, "route_id");
        var methodName = ReadString(payload, "method_name");
        var parameters = ReadString(payload, "parameters_base64");
        var mode = ReadString(payload, "delivery_mode");
        if (!payload.TryGetProperty("message_id", out var messageElement) ||
            !messageElement.TryGetInt64(out var messageId) ||
            !payload.TryGetProperty("sender_peer_id", out var senderElement) ||
            !senderElement.TryGetInt64(out var senderPeerId) ||
            !payload.TryGetProperty("target_peer_id", out var targetElement) ||
            !targetElement.TryGetInt64(out var targetPeerId) ||
            !payload.TryGetProperty("target_zdo_user_id", out var zdoUserElement) ||
            !zdoUserElement.TryGetInt64(out var targetZdoUserId) ||
            !payload.TryGetProperty("target_zdo_id", out var zdoIdElement) ||
            !zdoIdElement.TryGetUInt32(out var targetZdoId) ||
            !payload.TryGetProperty("method_hash", out var methodElement) ||
            !methodElement.TryGetInt32(out var methodHash) ||
            !SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
            !SafeToken(routeId, 192) ||
            mode is not ("deliver" or "withhold") ||
            session.ValheimPeerUid != senderPeerId ||
            messageId == 0 ||
            !AllowedRoutedMethod(methodName, methodHash) ||
            parameters.Length > 48_000)
            throw new InvalidDataException("invalid Valheim routed RPC envelope");

        try
        {
            if (Convert.FromBase64String(parameters).Length > 32_768)
                throw new InvalidDataException("Valheim routed RPC parameters exceed limit");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Valheim routed RPC parameters are not base64");
        }

        if (!session.Reliable.TryAcceptClientMessage(
                envelope.Seq, $"routed:{routeId}"))
        {
            _logger.LogInformation(
                "Valheim routed duplicate ignored connection={ConnectionId} route={RouteId}",
                session.ConnectionId, routeId);
            return;
        }

        IReadOnlyCollection<GameSession> targets;
        if (targetPeerId == 0)
        {
            targets = _sessions.GetAll()
                .Where(candidate =>
                    candidate.SessionId != session.SessionId &&
                    candidate.ValheimPeerUid.HasValue)
                .ToArray();
        }
        else
        {
            var target = _sessions.FindByValheimPeer(targetPeerId);
            targets = target == null
                ? Array.Empty<GameSession>()
                : new[] { target };
        }
        if (targets.Count == 0)
        {
            await SendErrorAsync(session, "VALHEIM_ROUTE_TARGET_MISSING", routeId);
            return;
        }

        if (mode == "withhold")
        {
            _logger.LogInformation(
                "Valheim routed delivery intentionally withheld route={RouteId} method={Method} targets={TargetCount}",
                routeId, methodName, targets.Count);
            return;
        }

        foreach (var target in targets)
        {
            var queued = await target.SendReliableAsync(
                MessageType.ValheimRoutedRpc,
                new
                {
                    run_id = runId,
                    action_id = actionId,
                    route_id = routeId,
                    message_id = messageId,
                    sender_peer_id = senderPeerId,
                    target_peer_id = targetPeerId,
                    target_zdo_user_id = targetZdoUserId,
                    target_zdo_id = targetZdoId,
                    method_name = methodName,
                    method_hash = methodHash,
                    parameters_base64 = parameters,
                    source_sequence = envelope.Seq,
                },
                CancellationToken.None);
            if (!queued.Queued)
            {
                await SendErrorAsync(session, "RELIABLE_BACKPRESSURE", queued.Reason);
                return;
            }
        }
        _logger.LogInformation(
            "Valheim routed RPC queued route={RouteId} method={Method} sender={Sender} target={Target} target_zdo={TargetZdoUser}:{TargetZdoId} recipients={RecipientCount}",
            routeId, methodName, senderPeerId, targetPeerId,
            targetZdoUserId, targetZdoId, targets.Count);
    }

    async Task HandleValheimZdoMutationAsync(GameSession session, Envelope envelope)
    {
        if (!string.Equals(session.ValheimRole, "server", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId))
            throw new InvalidDataException("ZDO mutation requires the logical server session");

        var mutation = JsonSerializer.Deserialize<ValheimZdoJournalObject>(
            envelope.Payload.GetRawText(), JsonOptions.Default)
            ?? throw new InvalidDataException("ZDO mutation payload missing");
        var validation = ValheimZdoJournalEndpoints.ValidateMutation(mutation);
        if (validation is not null)
            throw new InvalidDataException(validation);
        var idempotencyKey =
            $"zdo-mutation:{mutation.WorldEpoch}:{mutation.UidUser}:{mutation.UidId}:{mutation.SourceSequence}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, idempotencyKey))
        {
            _logger.LogInformation(
                "Duplicate Valheim ZDO mutation ignored logical_peer={LogicalPeer} source_sequence={SourceSequence}",
                session.ValheimLogicalPeerId, mutation.SourceSequence);
            return;
        }

        var result = _zdoJournal.Record(mutation);
        if (mutation.ReceiptRequired)
        {
            var receipt = await session.SendReliableAsync(
                MessageType.ValheimZdoMutationReceipt,
                new
                {
                    run_id = mutation.RunId,
                    world_epoch = mutation.WorldEpoch,
                    source_sequence = mutation.SourceSequence,
                    object_revision = mutation.ObjectRevision,
                    accepted = result.Accepted,
                    result = result.Result,
                    recipient_count = result.RecipientCount,
                    durable_objects = result.DurableObjects,
                    logical_peer_id = session.ValheimLogicalPeerId,
                },
                CancellationToken.None);
            if (!receipt.Queued)
                throw new InvalidOperationException(receipt.Reason);
        }
        if (!result.Accepted)
            return;

        foreach (var target in _sessions.GetAll().Where(candidate =>
                     string.Equals(candidate.ValheimRole, "client", StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(candidate.ValheimLogicalPeerId)))
            await SendPendingZdoDeliveriesAsync(target, mutation.WorldEpoch);
    }

    async Task HandleValheimZdoInterestAsync(GameSession session, Envelope envelope)
    {
        if (!string.Equals(session.ValheimRole, "client", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId))
            throw new InvalidDataException("ZDO interest requires a logical client session");

        var supplied = JsonSerializer.Deserialize<ValheimZdoJournalInterest>(
            envelope.Payload.GetRawText(), JsonOptions.Default)
            ?? throw new InvalidDataException("ZDO interest payload missing");
        var interest = supplied with { RecipientId = session.ValheimLogicalPeerId };
        var validation = ValheimZdoJournalEndpoints.ValidateInterest(interest);
        if (validation is not null)
            throw new InvalidDataException(validation);
        var idempotencyKey =
            $"zdo-interest:{interest.WorldEpoch}:{interest.RunId}:{interest.ZoneEpoch}:{interest.ZoneX}:{interest.ZoneY}:{interest.RadiusZones}:{interest.Refresh}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, idempotencyKey))
            return;

        var result = _zdoJournal.RegisterInterest(session.ValheimLogicalPeerId, interest);
        var receipt = await session.SendReliableAsync(
            MessageType.ValheimZdoInterestReceipt,
            new
            {
                run_id = interest.RunId,
                world_epoch = interest.WorldEpoch,
                logical_peer_id = session.ValheimLogicalPeerId,
                snapshot_count = result.SnapshotCount,
                pending = result.PendingCount,
            },
            CancellationToken.None);
        if (!receipt.Queued)
            throw new InvalidOperationException(receipt.Reason);

        await SendPendingZdoDeliveriesAsync(session, interest.WorldEpoch);
        var status = _zdoJournal.RunStatus(interest.RunId, interest.WorldEpoch);
        foreach (var server in _sessions.GetAll().Where(candidate =>
                     string.Equals(candidate.ValheimRole, "server", StringComparison.Ordinal)))
        {
            var queued = await server.SendReliableAsync(
                MessageType.ValheimZdoInterestStatus,
                new
                {
                    run_id = interest.RunId,
                    world_epoch = interest.WorldEpoch,
                    recipient_id = session.ValheimLogicalPeerId,
                    zone_epoch = interest.ZoneEpoch,
                    zone_x = interest.ZoneX,
                    zone_y = interest.ZoneY,
                    radius_zones = interest.RadiusZones,
                    interested_recipients = status.InterestedRecipients,
                    pending = status.Pending,
                    durable_objects = status.DurableObjects,
                },
                CancellationToken.None);
            if (!queued.Queued)
                throw new InvalidOperationException(queued.Reason);
        }
    }

    void HandleValheimZdoAck(GameSession session, Envelope envelope)
    {
        if (!string.Equals(session.ValheimRole, "client", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId))
            throw new InvalidDataException("ZDO ACK requires a logical client session");
        var ack = JsonSerializer.Deserialize<ValheimZdoJournalAck>(
            envelope.Payload.GetRawText(), JsonOptions.Default)
            ?? throw new InvalidDataException("ZDO ACK payload missing");
        if (!SafeToken(ack.WorldEpoch, 96) ||
            ack.Sequences is null || ack.Sequences.Length is < 1 or > 1024 ||
            ack.Sequences.Any(value => value <= 0))
            throw new InvalidDataException("invalid ZDO ACK");
        var idempotencyKey =
            $"zdo-ack:{ack.WorldEpoch}:{string.Join(',', ack.Sequences.Order())}";
        if (!session.Reliable.TryAcceptClientMessage(envelope.Seq, idempotencyKey))
            return;
        var result = _zdoJournal.Acknowledge(
            session.ValheimLogicalPeerId, ack.WorldEpoch, ack.Sequences);
        _logger.LogInformation(
            "Valheim ZDO ACK logical_peer={LogicalPeer} world={WorldEpoch} acknowledged={Acknowledged} unknown={Unknown}",
            session.ValheimLogicalPeerId, ack.WorldEpoch,
            result.Acknowledged, result.Unknown);
    }

    async Task HandleValheimOwnershipLeaseRequestAsync(
        GameSession session, Envelope envelope)
    {
        if (session.ValheimRole != "client" ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId) ||
            !session.ValheimPeerUid.HasValue)
            throw new InvalidDataException(
                "ownership lease request requires a bound logical client");
        var payload = envelope.Payload;
        var runId = ReadString(payload, "run_id");
        var actionId = ReadString(payload, "action_id");
        var attemptId = ReadString(payload, "attempt_id");
        var phase = ReadString(payload, "phase");
        var requestOrdinal = ReadInt32(payload, "request_ordinal");
        var worldEpoch = ReadString(payload, "world_epoch");
        var uidUser = ReadInt64(payload, "uid_user");
        var uidId = ReadUInt32(payload, "uid_id");
        var x = ReadDouble(payload, "origin_x");
        var y = ReadDouble(payload, "origin_y");
        var z = ReadDouble(payload, "origin_z");
        if (!SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
            !SafeToken(worldEpoch, 96) ||
            phase is not ("create" or "reissue" or "contend") ||
            (phase == "create" && requestOrdinal != 0) ||
            (phase == "reissue" && requestOrdinal is < 1 or > 100) ||
            (phase == "contend" &&
                (!SafeToken(attemptId, 96) || requestOrdinal is < 1 or > 100)) ||
            ((phase is "create" or "contend") &&
                (uidUser != 0 || uidId != 0)) ||
            (phase == "reissue" && (uidUser == 0 || uidId == 0)) ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
            Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000 ||
            Math.Abs(z) > 1_000_000)
            throw new InvalidDataException("invalid ownership lease request");
        var previousClientSequence = session.Reliable.LastClientSequence;
        var accepted = session.Reliable.TryAcceptClientMessage(
            envelope.Seq,
            $"ownership-request:{runId}:{actionId}:{phase}:{requestOrdinal}:{uidUser}:{uidId}");
        _logger.LogInformation(
            "Valheim ownership lease request connection={ConnectionId} logical_peer={LogicalPeer} action={ActionId} phase={Phase} client_sequence={ClientSequence} previous_client_sequence={PreviousClientSequence} accepted={Accepted}",
            session.Reliable.ConnectionId, session.ValheimLogicalPeerId,
            actionId, phase, envelope.Seq, previousClientSequence, accepted);
        if (!accepted)
            return;
        if (phase == "contend")
        {
            var lease = _ownershipLeases.FindByAction(runId, actionId);
            var validation = lease is null
                ? new ValheimOwnershipLeaseService.Validation(
                    false, "lease_missing", null)
                : _ownershipLeases.Validate(
                    runId, lease.WorldEpoch, lease.UidUser, lease.UidId,
                    session.ValheimLogicalPeerId, lease.Epoch,
                    DateTimeOffset.UtcNow);
            var reason = validation.Accepted
                ? "holder_not_distinct"
                : validation.Reason;
            var rejected = await session.SendReliableAsync(
                MessageType.ValheimOwnershipActionRejected,
                new
                {
                    run_id = runId,
                    action_id = actionId,
                    attempt_id = attemptId,
                    world_epoch = worldEpoch,
                    uid_user = lease?.UidUser ?? 0,
                    uid_id = lease?.UidId ?? 0,
                    lease_epoch = lease?.Epoch ?? 0,
                    lease_state = lease?.State ?? "missing",
                    reason,
                },
                CancellationToken.None);
            if (!rejected.Queued)
                throw new InvalidOperationException(rejected.Reason);
            return;
        }
        var server = _sessions.GetAll().SingleOrDefault(candidate =>
            candidate.ValheimRole == "server" &&
            candidate.ValheimPeerUid.HasValue);
        if (server is null)
        {
            await SendErrorAsync(
                session, "OWNERSHIP_SERVER_MISSING", actionId);
            return;
        }
        var queued = await server.SendReliableAsync(
            MessageType.ValheimOwnershipLeaseCommand,
            new
            {
                run_id = runId,
                action_id = actionId,
                phase,
                request_ordinal = requestOrdinal,
                world_epoch = worldEpoch,
                uid_user = uidUser,
                uid_id = uidId,
                holder_logical_peer_id = session.ValheimLogicalPeerId,
                holder_peer_uid = session.ValheimPeerUid.Value,
                origin_x = x,
                origin_y = y,
                origin_z = z,
            },
            CancellationToken.None);
        if (!queued.Queued)
            throw new InvalidOperationException(queued.Reason);
    }

    async Task HandleValheimOwnershipLeaseIssueAsync(
        GameSession session, Envelope envelope)
    {
        if (session.ValheimRole != "server" ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId))
            throw new InvalidDataException(
                "ownership lease issue requires the logical server");
        var payload = envelope.Payload;
        var runId = ReadString(payload, "run_id");
        var actionId = ReadString(payload, "action_id");
        var phase = ReadString(payload, "phase");
        var requestOrdinal = ReadInt32(payload, "request_ordinal");
        var worldEpoch = ReadString(payload, "world_epoch");
        var holderLogical = ReadString(payload, "holder_logical_peer_id");
        var holderPeerUid = ReadInt64(payload, "holder_peer_uid");
        var uidUser = ReadInt64(payload, "uid_user");
        var uidId = ReadUInt32(payload, "uid_id");
        var duration = ReadInt32(payload, "duration_seconds");
        var itemPrefab = ReadString(payload, "item_prefab");
        if (!SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
            !SafeToken(worldEpoch, 96) || !SafeToken(holderLogical, 80) ||
            !SafeToken(itemPrefab, 80) ||
            phase is not ("create" or "reissue") ||
            (phase == "create" && requestOrdinal != 0) ||
            (phase == "reissue" && requestOrdinal is < 1 or > 100) ||
            holderPeerUid == 0 || uidUser == 0 || uidId == 0)
            throw new InvalidDataException("invalid ownership lease issue");
        var holder = _sessions.FindByValheimLogicalPeer(holderLogical);
        if (holder is null || holder.ValheimRole != "client" ||
            holder.ValheimPeerUid != holderPeerUid)
            throw new InvalidDataException("ownership lease holder is not active");
        if (!session.Reliable.TryAcceptClientMessage(
                envelope.Seq,
                $"ownership-issue:{runId}:{actionId}:{phase}:{requestOrdinal}:{uidUser}:{uidId}"))
            return;
        var lease = _ownershipLeases.Issue(
            runId, worldEpoch, uidUser, uidId, holderLogical, holderPeerUid,
            actionId, duration, DateTimeOffset.UtcNow);
        var grant = await holder.SendReliableAsync(
            MessageType.ValheimOwnershipLeaseGranted,
            new
            {
                run_id = runId,
                action_id = actionId,
                phase,
                request_ordinal = requestOrdinal,
                world_epoch = worldEpoch,
                uid_user = uidUser,
                uid_id = uidId,
                holder_logical_peer_id = holderLogical,
                holder_peer_uid = holderPeerUid,
                lease_epoch = lease.Epoch,
                issued_utc = lease.IssuedUtc,
                expires_utc = lease.ExpiresUtc,
                item_prefab = itemPrefab,
            },
            CancellationToken.None);
        if (!grant.Queued)
            throw new InvalidOperationException(grant.Reason);
        var receipt = await session.SendReliableAsync(
            MessageType.ValheimOwnershipLeaseReceipt,
            new
            {
                run_id = runId,
                action_id = actionId,
                phase,
                request_ordinal = requestOrdinal,
                world_epoch = worldEpoch,
                uid_user = uidUser,
                uid_id = uidId,
                holder_logical_peer_id = holderLogical,
                lease_epoch = lease.Epoch,
                expires_utc = lease.ExpiresUtc,
            },
            CancellationToken.None);
        if (!receipt.Queued)
            throw new InvalidOperationException(receipt.Reason);
    }

    async Task HandleValheimOwnershipActionAsync(
        GameSession session, Envelope envelope)
    {
        if (session.ValheimRole != "client" ||
            string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId) ||
            !session.ValheimPeerUid.HasValue)
            throw new InvalidDataException(
                "ownership action requires a bound logical client");
        var payload = envelope.Payload;
        var runId = ReadString(payload, "run_id");
        var actionId = ReadString(payload, "action_id");
        var attemptId = ReadString(payload, "attempt_id");
        var worldEpoch = ReadString(payload, "world_epoch");
        var kind = ReadString(payload, "action_kind");
        var uidUser = ReadInt64(payload, "uid_user");
        var uidId = ReadUInt32(payload, "uid_id");
        var epoch = ReadInt64(payload, "lease_epoch");
        if (!SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
            !SafeToken(attemptId, 96) || !SafeToken(worldEpoch, 96) ||
            kind != "pickup" || uidUser == 0 || uidId == 0 || epoch < 1)
            throw new InvalidDataException("invalid ownership action");
        if (!session.Reliable.TryAcceptClientMessage(
                envelope.Seq, $"ownership-action:{attemptId}"))
            return;
        var validation = _ownershipLeases.Validate(
            runId, worldEpoch, uidUser, uidId,
            session.ValheimLogicalPeerId, epoch, DateTimeOffset.UtcNow);
        if (!validation.Accepted)
        {
            var rejected = await session.SendReliableAsync(
                MessageType.ValheimOwnershipActionRejected,
                new
                {
                    run_id = runId,
                    action_id = actionId,
                    attempt_id = attemptId,
                    world_epoch = worldEpoch,
                    uid_user = uidUser,
                    uid_id = uidId,
                    lease_epoch = epoch,
                    reason = validation.Reason,
                },
                CancellationToken.None);
            if (!rejected.Queued)
                throw new InvalidOperationException(rejected.Reason);
            return;
        }
        var server = _sessions.GetAll().SingleOrDefault(candidate =>
            candidate.ValheimRole == "server" &&
            candidate.ValheimPeerUid.HasValue);
        if (server is null)
            throw new InvalidDataException("ownership server is not active");
        var authorized = await server.SendReliableAsync(
            MessageType.ValheimOwnershipActionAuthorized,
            new
            {
                run_id = runId,
                action_id = actionId,
                attempt_id = attemptId,
                action_kind = kind,
                world_epoch = worldEpoch,
                uid_user = uidUser,
                uid_id = uidId,
                lease_epoch = epoch,
                holder_logical_peer_id = session.ValheimLogicalPeerId,
                holder_peer_uid = session.ValheimPeerUid.Value,
            },
            CancellationToken.None);
        if (!authorized.Queued)
            throw new InvalidOperationException(authorized.Reason);
    }

    async Task HandleValheimOwnershipActionResultAsync(
        GameSession session, Envelope envelope)
    {
        if (session.ValheimRole != "server")
            throw new InvalidDataException(
                "ownership action result requires the logical server");
        var payload = envelope.Payload;
        var runId = ReadString(payload, "run_id");
        var actionId = ReadString(payload, "action_id");
        var attemptId = ReadString(payload, "attempt_id");
        var worldEpoch = ReadString(payload, "world_epoch");
        var holderLogical = ReadString(payload, "holder_logical_peer_id");
        var uidUser = ReadInt64(payload, "uid_user");
        var uidId = ReadUInt32(payload, "uid_id");
        var epoch = ReadInt64(payload, "lease_epoch");
        var success = payload.TryGetProperty("success", out var successElement) &&
            successElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            successElement.GetBoolean();
        var itemPrefab = ReadString(payload, "item_prefab");
        var itemCount = ReadInt32(payload, "item_count");
        if (!success || !SafeToken(runId, 80) || !SafeToken(actionId, 80) ||
            !SafeToken(attemptId, 96) || !SafeToken(worldEpoch, 96) ||
            !SafeToken(holderLogical, 80) || !SafeToken(itemPrefab, 80) ||
            uidUser == 0 || uidId == 0 || epoch < 1 ||
            itemCount is < 1 or > 100)
            throw new InvalidDataException("invalid ownership action result");
        if (!session.Reliable.TryAcceptClientMessage(
                envelope.Seq, $"ownership-result:{attemptId}"))
            return;
        var lease = _ownershipLeases.Complete(
            runId, worldEpoch, uidUser, uidId, holderLogical, epoch,
            DateTimeOffset.UtcNow);
        var holder = _sessions.FindByValheimLogicalPeer(holderLogical)
            ?? throw new InvalidDataException("ownership result holder is not active");
        var completed = await holder.SendReliableAsync(
            MessageType.ValheimOwnershipActionCompleted,
            new
            {
                run_id = runId,
                action_id = actionId,
                attempt_id = attemptId,
                world_epoch = worldEpoch,
                uid_user = uidUser,
                uid_id = uidId,
                lease_epoch = epoch,
                holder_logical_peer_id = holderLogical,
                item_prefab = itemPrefab,
                item_count = itemCount,
                result = "authoritative_pickup",
            },
            CancellationToken.None);
        if (!completed.Queued)
            throw new InvalidOperationException(completed.Reason);
        var receipt = await session.SendReliableAsync(
            MessageType.ValheimOwnershipResultReceipt,
            new
            {
                run_id = runId,
                action_id = actionId,
                attempt_id = attemptId,
                world_epoch = worldEpoch,
                uid_user = uidUser,
                uid_id = uidId,
                lease_epoch = lease.Epoch,
                holder_logical_peer_id = holderLogical,
                result = "completed",
            },
            CancellationToken.None);
        if (!receipt.Queued)
            throw new InvalidOperationException(receipt.Reason);
    }

    async Task SendPendingZdoDeliveriesAsync(GameSession target, string worldEpoch)
    {
        const int controlHeadroom = 32;
        foreach (var delivery in _zdoJournal.Pending(
                     target.ValheimLogicalPeerId, worldEpoch, 1024))
        {
            if (target.Reliable.PendingCount >=
                ReliableGameSessionState.MaxPendingFrames - controlHeadroom)
                break;
            if (!target.TryMarkZdoDelivery(worldEpoch, delivery.Sequence))
                continue;
            var queued = await target.SendReliableAsync(
                MessageType.ValheimZdoDelivery,
                new
                {
                    seq = delivery.Sequence,
                    kind = delivery.Kind,
                    @object = delivery.Object,
                    logical_peer_id = target.ValheimLogicalPeerId,
                },
                CancellationToken.None);
            if (queued.Queued) continue;
            target.UnmarkZdoDelivery(worldEpoch, delivery.Sequence);
            if (string.Equals(
                    queued.Reason, "reliable_send_queue_full",
                    StringComparison.Ordinal))
                break;
            throw new InvalidOperationException(queued.Reason);
        }
    }

    static string ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var element)
            ? element.GetString() ?? string.Empty
            : string.Empty;

    static long ReadInt64(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var element) &&
        element.TryGetInt64(out var value)
            ? value
            : throw new InvalidDataException(name + " must be int64");

    static uint ReadUInt32(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var element) &&
        element.TryGetUInt32(out var value)
            ? value
            : throw new InvalidDataException(name + " must be uint32");

    static int ReadInt32(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var element) &&
        element.TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException(name + " must be int32");

    static double ReadDouble(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var element) &&
        element.TryGetDouble(out var value)
            ? value
            : throw new InvalidDataException(name + " must be number");

    static bool FiniteAndBounded(double value, double absoluteMaximum) =>
        double.IsFinite(value) && Math.Abs(value) <= absoluteMaximum;

    static bool AllowedRoutedMethod(string methodName, int methodHash)
    {
        if (methodName is not (
                "ComfyNetworkSense_CutoverRoutedRequest" or
                "ComfyNetworkSense_CutoverRoutedResponse" or
                "ComfyNetworkSense_CutoverRoutedBroadcastRequest" or
                "ComfyNetworkSense_CutoverRoutedBroadcast" or
                "ComfyNetworkSense_CutoverRoutedTargetReceipt" or
                "ComfyNetworkSense_CutoverZdoJournalRequest" or
                "RPC_ResetCloth"))
            return false;
        return StableHash(methodName) == methodHash;
    }

    static int StableHash(string value)
    {
        unchecked
        {
            var first = 5381;
            var second = first;
            for (var index = 0; index < value.Length && value[index] != 0; index += 2)
            {
                first = ((first << 5) + first) ^ value[index];
                if (index == value.Length - 1 || value[index + 1] == 0) break;
                second = ((second << 5) + second) ^ value[index + 1];
            }
            return first + second * 1566083941;
        }
    }

    static bool SafeToken(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.');


    /// <summary>
    /// Sends a fresh world_snapshot to a resumed session (re-sync after reconnect).
    /// </summary>
    public async Task SendWorldSnapshotAsync(GameSession session)
    {
        if (session.RegionId == null) return;

        try
        {
            var joinResult = _playerHandler.Join(new JoinRequest
            {
                PlayerId = session.PlayerId,
                RegionId = session.RegionId,
                GuildId = session.GuildId,
            });

            if (joinResult.Success)
            {
                var snapshot = new
                {
                    region_id = session.RegionId,
                    entities = joinResult.Entities,
                    tick = 0,
                    region_profile = BuildRegionProfilePayload(session.RegionId),
                };

                var snapshotEnvelope = EnvelopeFactory.Create(MessageType.WorldSnapshot, snapshot);
                await SendToSessionAsync(session, snapshotEnvelope);

                // Notify others in the region
                var playerUpdate = new
                {
                    entity_id = session.PlayerId,
                    entity_type = "player",
                    data = new Dictionary<string, object>
                    {
                        ["player_id"] = session.PlayerId,
                        ["name"] = $"Player-{session.PlayerId[..8]}",
                        ["position"] = new { x = 0, y = 0, z = 0 },
                        ["connected"] = true,
                    },
                    tick = 0,
                };
                var updateEnvelope = EnvelopeFactory.Create(MessageType.EntityUpdate, playerUpdate);
                await BroadcastToRegionAsync(session.RegionId, session.SessionId, updateEnvelope);

                _logger.LogInformation("Player {PlayerId} re-joined {RegionId} after resume",
                    session.PlayerId, session.RegionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send world snapshot on resume for {PlayerId}", session.PlayerId);
        }
    }

    public async Task HandleDisconnectAsync(GameSession session)
    {
        if (_valheimPlayers != null)
            await _valheimPlayers.SessionDisconnectedAsync(session);

        if (session.ValheimPeerUid.HasValue &&
            !string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId))
        {
            var counterparts = _sessions.GetAll()
                .Where(candidate =>
                    candidate.SessionId != session.SessionId &&
                    candidate.ValheimPeerUid.HasValue &&
                    !string.IsNullOrWhiteSpace(candidate.ValheimLogicalPeerId) &&
                    !string.Equals(
                        candidate.ValheimRole,
                        session.ValheimRole,
                        StringComparison.Ordinal))
                .ToArray();
            foreach (var counterpart in counterparts)
            {
                var detached = await counterpart.SendReliableAsync(
                    MessageType.ValheimLogicalPeerDetached,
                    LogicalPeerPayload(session),
                    CancellationToken.None);
                if (!detached.Queued)
                    _logger.LogWarning(
                        "Could not queue logical peer detach source={LogicalPeer} target={Target}: {Reason}",
                        session.ValheimLogicalPeerId,
                        counterpart.ValheimLogicalPeerId,
                        detached.Reason);
            }
        }
        await HandleLeaveRegionAsync(session);
    }

    private async Task HandleJoinRegionAsync(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;

        var regionId = payload.GetProperty("region_id").GetString() ?? "region-spawn";
        var guildId = payload.TryGetProperty("guild_id", out var gidEl) ? gidEl.GetString() : null;

        // Leave current region first if switching
        if (session.RegionId != null && session.RegionId != regionId)
            await HandleLeaveRegionAsync(session);

        // Store guild_id on the session so downstream actions can use it
        if (!string.IsNullOrEmpty(guildId))
            session.GuildId = guildId;

        var result = _playerHandler.Join(new JoinRequest
        {
            PlayerId = session.PlayerId,
            RegionId = regionId,
            GuildId = session.GuildId,
        });

        if (result.Success)
        {
            // Track which region this session is in
            session.RegionId = regionId;

            // Send world_snapshot to the joining player
            var snapshot = new
            {
                region_id = regionId,
                entities = result.Entities,
                tick = 0,
                region_profile = BuildRegionProfilePayload(regionId),
            };

            var snapshotEnvelope = EnvelopeFactory.Create(MessageType.WorldSnapshot, snapshot);
            await SendToSessionAsync(session, snapshotEnvelope);

            // Broadcast entity_update for the new player to everyone else in the region
            var playerUpdate = new
            {
                entity_id = session.PlayerId,
                entity_type = "player",
                data = new Dictionary<string, object>
                {
                    ["player_id"] = session.PlayerId,
                    ["name"] = $"Player-{session.PlayerId[..8]}",
                    ["position"] = new { x = 0, y = 0, z = 0 },
                    ["connected"] = true,
                },
                tick = 0,
            };

            var updateEnvelope = EnvelopeFactory.Create(MessageType.EntityUpdate, playerUpdate);
            await BroadcastToRegionAsync(regionId, session.SessionId, updateEnvelope);

            _logger.LogInformation("Player {PlayerId} joined {RegionId}", session.PlayerId, regionId);
        }
        else
        {
            await SendErrorAsync(session, "JOIN_FAILED", $"Join failed: {result.Error}");
        }
    }

    /// <summary>
    /// New input-driven path: enqueue raw input directly into the simulation's InputQueue.
    /// No HTTP roundtrip — this is the key scalability improvement.
    /// The TickLoop will process this input on the next tick.
    /// </summary>
    private void HandlePlayerInput(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;

        var direction = payload.TryGetProperty("direction", out var dirEl) ? dirEl.GetByte() : (byte)0;
        var speedPercent = payload.TryGetProperty("speed_percent", out var spdEl) ? spdEl.GetByte() : (byte)0;
        var actionFlags = payload.TryGetProperty("action_flags", out var actEl) ? actEl.GetByte() : (byte)0;
        var inputSeq = payload.TryGetProperty("input_seq", out var seqEl) ? (ushort)seqEl.GetUInt32() : (ushort)0;

        EnqueueInput(session.PlayerId, direction, speedPercent, actionFlags, inputSeq);
    }

    /// <summary>
    /// Binary input path: called directly from middleware when a binary player_input frame arrives.
    /// Skips JSON deserialization entirely.
    /// </summary>
    public void HandlePlayerInputBinary(GameSession session, PlayerInputBinary input, string transport = "websocket-binary")
    {
        LumberjacksTelemetry.RecordMessage(MessageType.PlayerInput, transport);
        EnqueueInput(session.PlayerId, input.Direction, input.SpeedPercent, input.ActionFlags, input.InputSeq);
    }

    private void EnqueueInput(string playerId, byte direction, byte speedPercent, byte actionFlags, ushort inputSeq)
    {
        var input = new PlayerInputMessage
        {
            Direction = direction,
            SpeedPercent = speedPercent,
            ActionFlags = actionFlags,
            InputSeq = inputSeq,
        };

        _inputQueue.Enqueue(playerId, input, _world.CurrentTick);
    }

    /// <summary>
    /// Legacy path: accepts absolute positions. Kept for backwards compatibility.
    /// New clients should use player_input. Movement broadcasting is handled by TickBroadcaster.
    /// </summary>
    private async Task HandlePlayerMoveAsync(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;

        var position = payload.GetProperty("position");
        var velocity = payload.TryGetProperty("velocity", out var vel) ? vel : default;

        var posVec = new Game.Contracts.Entities.Vec3(
            position.GetProperty("x").GetDouble(),
            position.GetProperty("y").GetDouble(),
            position.GetProperty("z").GetDouble());

        var velVec = new Game.Contracts.Entities.Vec3(
            velocity.ValueKind != JsonValueKind.Undefined ? velocity.GetProperty("x").GetDouble() : 0.0,
            velocity.ValueKind != JsonValueKind.Undefined ? velocity.GetProperty("y").GetDouble() : 0.0,
            velocity.ValueKind != JsonValueKind.Undefined ? velocity.GetProperty("z").GetDouble() : 0.0);

        var result = _playerHandler.Move(new MoveRequest
        {
            PlayerId = session.PlayerId,
            Position = posVec,
            Velocity = velVec,
        });

        if (result.Success && result.Corrected)
        {
            // Send correction back to the mover
            var correction = new
            {
                entity_id = session.PlayerId,
                entity_type = "player",
                data = new Dictionary<string, object>
                {
                    ["player_id"] = session.PlayerId,
                    ["position"] = new { x = result.Position.X, y = result.Position.Y, z = result.Position.Z },
                    ["velocity"] = new { x = result.Velocity.X, y = result.Velocity.Y, z = result.Velocity.Z },
                    ["corrected"] = true,
                },
                tick = 0,
            };
            var corrEnvelope = EnvelopeFactory.Create(MessageType.EntityUpdate, correction);
            await SendToSessionAsync(session, corrEnvelope);
        }
    }

    private async Task HandleLeaveRegionAsync(GameSession session)
    {
        var leavingRegion = session.RegionId;

        var result = _playerHandler.Leave(new LeaveRequest
        {
            PlayerId = session.PlayerId,
        });

        if (result.Removed)
        {
            // Broadcast entity_removed to players in the region the player was in
            var removeEnvelope = EnvelopeFactory.Create(MessageType.EntityRemoved, new
            {
                entity_id = session.PlayerId,
                tick = 0,
            });
            await BroadcastToRegionAsync(leavingRegion, session.SessionId, removeEnvelope);
        }

        session.RegionId = null;
    }

    private async Task HandlePlaceStructureAsync(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;
        var structureType = payload.GetProperty("structure_type").GetString() ?? "unknown";
        var position = payload.GetProperty("position");
        var rotation = payload.TryGetProperty("rotation", out var rotEl) ? rotEl.GetDouble() : 0.0;

        var result = await _placeStructureHandler.HandleAsync(new PlaceStructureRequest
        {
            PlayerId = session.PlayerId,
            RegionId = session.RegionId ?? "region-spawn",
            StructureType = structureType,
            Position = new Game.Contracts.Entities.Vec3(
                position.GetProperty("x").GetDouble(),
                position.GetProperty("y").GetDouble(),
                position.GetProperty("z").GetDouble()),
            Rotation = rotation,
            GuildId = session.GuildId,
        });

        if (result.Success)
        {
            var s = result.Structure!;
            var entityUpdate = new
            {
                entity_id = s.Id,
                entity_type = "structure",
                data = new Dictionary<string, object>
                {
                    ["structure_id"] = s.Id,
                    ["type"] = s.Type,
                    ["position"] = new { x = s.Position.X, y = s.Position.Y, z = s.Position.Z },
                    ["rotation"] = s.Rotation,
                    ["owner_id"] = s.OwnerId,
                    ["region_id"] = s.RegionId,
                    ["placed_at"] = s.PlacedAt,
                    ["tags"] = s.Tags,
                },
                tick = 0,
            };

            var updateEnvelope = EnvelopeFactory.Create(MessageType.EntityUpdate, entityUpdate);
            await SendToSessionAsync(session, updateEnvelope);
            await BroadcastToRegionAsync(session.RegionId, session.SessionId, updateEnvelope);

            _logger.LogInformation("Structure placed by {PlayerId}: {StructureId}",
                session.PlayerId, s.Id);
        }
        else
        {
            await SendErrorAsync(session, "PLACEMENT_FAILED", $"Structure placement failed: {result.Error}");
        }
    }

    private async Task HandleInteractAsync(GameSession session, Envelope envelope)
    {
        var payload = envelope.Payload;
        var action = payload.GetProperty("action").GetString();

        switch (action)
        {
            case "pickup":
            {
                var itemId = payload.GetProperty("item_id").GetString()!;
                var result = await _inventoryHandler.PickupItemAsync(session.PlayerId, itemId);

                if (result.Success)
                {
                    var pickupEnv = EnvelopeFactory.Create(MessageType.EventEmitted, new
                    {
                        event_type = "item_picked_up",
                        item_id = itemId,
                        item_type = result.Item!.ItemType,
                        quantity = result.Item.Quantity,
                    });
                    await SendToSessionAsync(session, pickupEnv);

                    var removeEnv = EnvelopeFactory.Create(MessageType.EntityRemoved, new
                    {
                        entity_id = itemId,
                        tick = 0,
                    });
                    await BroadcastToRegionAsync(session.RegionId, null, removeEnv);

                    _logger.LogInformation("Player {PlayerId} picked up item {ItemId}", session.PlayerId, itemId);
                }
                else
                {
                    await SendErrorAsync(session, "PICKUP_FAILED", result.Error!);
                }
                break;
            }

            case "store":
            {
                var containerId = payload.GetProperty("container_id").GetString()!;
                var itemType = payload.GetProperty("item_type").GetString()!;
                var quantity = payload.TryGetProperty("quantity", out var qtyEl) ? qtyEl.GetInt32() : 1;

                var result = await _inventoryHandler.StoreItemAsync(session.PlayerId, containerId, itemType, quantity);

                if (result.Success)
                {
                    var storeEnv = EnvelopeFactory.Create(MessageType.EventEmitted, new
                    {
                        event_type = "item_stored",
                        container_id = containerId,
                        item_type = itemType,
                        quantity,
                    });
                    await SendToSessionAsync(session, storeEnv);

                    _logger.LogInformation("Player {PlayerId} stored {ItemType} x{Qty} in {ContainerId}",
                        session.PlayerId, itemType, quantity, containerId);
                }
                else
                {
                    await SendErrorAsync(session, "STORE_FAILED", result.Error!);
                }
                break;
            }

            default:
                _logger.LogDebug("Unknown interact action: {Action}", action);
                break;
        }
    }

    private async Task SendToSessionAsync(GameSession session, Envelope envelope)
    {
        if (session.Socket.State != WebSocketState.Open) return;

        var json = EnvelopeFactory.Serialize(envelope);
        await session.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            CancellationToken.None);
    }

    private async Task SendErrorAsync(GameSession session, string code, string message)
    {
        var errEnvelope = EnvelopeFactory.Create(MessageType.Error, new ErrorMessage(code, message));
        await SendToSessionAsync(session, errEnvelope);
    }

    /// <summary>
    /// Broadcasts to all sessions in the given region, excluding the specified session.
    /// Falls back to global broadcast if regionId is null (for backwards compatibility).
    /// </summary>
    private async Task BroadcastToRegionAsync(string? regionId, string? excludeSessionId, Envelope envelope)
    {
        var json = EnvelopeFactory.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);

        var targets = regionId != null
            ? _sessions.GetByRegion(regionId)
            : _sessions.GetAll();

        foreach (var s in targets)
        {
            if (excludeSessionId != null && s.SessionId == excludeSessionId) continue;
            if (s.Socket.State != WebSocketState.Open) continue;

            try
            {
                await s.SendAsync(bytes, WebSocketMessageType.Text, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast to session {SessionId}", s.SessionId);
            }
        }
    }
}
