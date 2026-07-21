using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
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
    private readonly ILogger<MessageRouter> _logger;

    public MessageRouter(
        SessionManager sessions,
        InputQueue inputQueue,
        WorldState world,
        PlayerHandler playerHandler,
        PlaceStructureHandler placeStructureHandler,
        InventoryHandler inventoryHandler,
        ILogger<MessageRouter> logger)
    {
        _sessions = sessions;
        _inputQueue = inputQueue;
        _world = world;
        _playerHandler = playerHandler;
        _placeStructureHandler = placeStructureHandler;
        _inventoryHandler = inventoryHandler;
        _logger = logger;
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

            default:
                _logger.LogDebug("No route for message type {Type}", envelope.Type);
                break;
        }
    }

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
        await session.Socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            true,
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
                await s.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast to session {SessionId}", s.SessionId);
            }
        }
    }
}
