namespace Game.Gateway.Valheim;

/// <summary>
/// HTTP surface for the I5 / P6 handshake responder. The am4 mod hook (or the loopback
/// shim) drives the two decision points; /status feeds the MCP handshake gate.
/// </summary>
public static class ValheimHandshakeEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/valheim/handshake");

        // Configure the emulated dedicated-server context for a window (password, ban/whitelist,
        // player count, world reply fields). Optional — a window defaults to the am4 Steam-only
        // no-password shape.
        group.MapPost("/config", (ValheimHandshakeConfigRequest request,
            ValheimHandshakeService service) =>
        {
            var result = service.Configure(request.WindowId ?? string.Empty,
                request.Context ?? new ValheimHandshakeServerContext());
            return result.Ok
                ? Results.Ok(new { ok = true, window_id = request.WindowId })
                : Results.BadRequest(new { ok = false, error = result.Error });
        });

        // ServerHandshake ⇒ the ClientHandshake(needPassword, salt) the peer answers with.
        group.MapPost("/begin", (ValheimHandshakeBeginRequest request,
            ValheimHandshakeService service) =>
        {
            var result = service.Begin(request.WindowId ?? string.Empty,
                request.ConnectionId ?? string.Empty);
            return result.Ok
                ? Results.Ok(new { ok = true, window_id = request.WindowId,
                    connection_id = request.ConnectionId, client_handshake = result.Result })
                : Results.BadRequest(new { ok = false, error = result.Error });
        });

        // PeerInfo ⇒ accept (+ server PeerInfo) or reject (+ ConnectionStatus code).
        group.MapPost("/peerinfo", (ValheimPeerInfoSubmission submission,
            ValheimHandshakeService service) =>
        {
            var result = service.SubmitPeerInfo(GetWindow(submission), submission);
            return result.Ok
                ? Results.Ok(new { ok = true, connection_id = submission.ConnectionId,
                    gate = result.Result })
                : Results.BadRequest(new { ok = false, error = result.Error });
        });

        group.MapGet("/status", (ValheimHandshakeService service) =>
            Results.Ok(new { windows = service.GetAllStatuses() }));
        group.MapGet("/status/{windowId}", (string windowId, ValheimHandshakeService service) =>
            Results.Ok(service.GetStatus(windowId)));
        group.MapPost("/reset/{windowId}", (string windowId, ValheimHandshakeService service) =>
            Results.Ok(new { ok = true, window_id = windowId, reset = service.Reset(windowId) }));
        group.MapPost("/reset", (ValheimHandshakeService service) =>
            Results.Ok(new { ok = true, windows_cleared = service.ResetAll() }));
    }

    // PeerInfo submissions carry their window on the body so the mod can POST a bare PeerInfo;
    // fall back to the connection's own id namespace if omitted.
    private static string GetWindow(ValheimPeerInfoSubmission s) =>
        s.WindowId ?? string.Empty;
}

/// <summary>POST body for /valheim/handshake/begin.</summary>
public sealed record ValheimHandshakeBeginRequest
{
    public string? WindowId { get; init; }
    public string? ConnectionId { get; init; }
}
