using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Game.Gateway.BoundaryEvents;

public sealed class BoundaryRequestMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IBoundaryEventSink _sink;
    private readonly BoundaryEventSource _source;

    public BoundaryRequestMiddleware(RequestDelegate next, IBoundaryEventSink sink,
        IOptions<BoundaryEventOptions> options)
    {
        _next = next;
        _sink = sink;
        var value = options.Value;
        _source = new(value.Service, value.Instance, value.Release, null);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var activity = Activity.Current;
        var request = new BoundaryRequestContext
        {
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),
            Method = context.Request.Method,
            Route = context.Request.Path.Value ?? "/",
        };
        context.Items[BoundaryRequestContext.ItemKey] = request;
        Exception? failure = null;
        try { await _next(context); }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            request.Stopwatch.Stop();
            _sink.TryWrite(BoundaryEventEnvelope.Create("request.completed", _source, new
            {
                method = request.Method,
                route = request.Route,
                transport = context.Request.IsHttps ? "https" : "http",
                duration_ms = request.Stopwatch.Elapsed.TotalMilliseconds,
                status_code = context.Response.StatusCode,
                exception_type = failure?.GetType().Name,
            }, activity));
        }
    }
}
