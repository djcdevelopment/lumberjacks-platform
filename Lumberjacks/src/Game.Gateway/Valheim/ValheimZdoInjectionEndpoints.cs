namespace Game.Gateway.Valheim;

public static class ValheimZdoInjectionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/valheim/zdo-injection");

        group.MapPost("/stage", (ValheimZdoInjectionStageRequest request,
            ValheimZdoInjectionService service) =>
        {
            var result = service.Stage(request.WindowId ?? string.Empty, request.Command!);
            return result.Ok
                ? Results.Ok(new { ok = true, window_id = request.WindowId,
                    command_id = request.Command?.CommandId, duplicate = result.Duplicate })
                : Results.BadRequest(new { ok = false, error = result.Error });
        });

        group.MapGet("/next/{windowId}", (string windowId, string? client_id, HttpContext context,
            IConfiguration configuration,
            ValheimZdoInjectionService service) =>
        {
            var scope = Scope(context, client_id, configuration);
            if (scope.Error is not null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = service.Poll(windowId, scope.Resolved!);
            return result.Ok
                ? Results.Ok(new { ok = true, window_id = windowId, commands = result.Commands })
                : Results.BadRequest(new { ok = false, error = result.Error });
        }).RequireRateLimiting("consumer");

        group.MapPost("/ack", (ValheimZdoInjectionAckRequest request, HttpContext context,
            IConfiguration configuration,
            ValheimZdoInjectionService service) =>
        {
            var scope = Scope(context, request.ClientId, configuration);
            if (scope.Error is not null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = service.Ack(request with { ClientId = scope.Resolved });
            return result.Ok
                ? Results.Ok(new { ok = true, window_id = request.WindowId,
                    command_id = request.CommandId, client_id = request.ClientId })
                : Results.BadRequest(new { ok = false, error = result.Error });
        }).RequireRateLimiting("consumer");

        group.MapGet("/status", (ValheimZdoInjectionService service) =>
            Results.Ok(new { windows = service.GetAllStatuses() }));
        group.MapGet("/status/{windowId}", (string windowId, ValheimZdoInjectionService service) =>
            Results.Ok(service.GetStatus(windowId)));
        group.MapPost("/reset/{windowId}", (string windowId, ValheimZdoInjectionService service) =>
            Results.Ok(new { ok = true, window_id = windowId, reset = service.Reset(windowId) }));
        group.MapPost("/reset", (ValheimZdoInjectionService service) =>
            Results.Ok(new { ok = true, windows_cleared = service.ResetAll() }));
    }

    private static (string? Resolved, string? Error) Scope(
        HttpContext context, string? requested, IConfiguration configuration)
    {
        var principal = ValheimPrincipal.From(context);
        return ValheimRecipientScopePolicy.Resolve(principal?.Kind,
            principal?.Enrollment?.RecipientId, requested,
            configuration.GetValue("ValheimQueue:ProducerEmitsRecipients", false));
    }
}
