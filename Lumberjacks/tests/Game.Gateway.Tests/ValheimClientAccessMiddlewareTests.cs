using System.Net;
using Game.Gateway.Valheim;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// Gate M1 capability matrix: what each caller class may reach on the gated
/// Valheim control plane. Runs the real middleware against fabricated requests.
/// </summary>
public sealed class ValheimClientAccessMiddlewareTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "lumberjacks-mw-" + Guid.NewGuid().ToString("N"));
    const string SharedKey = "legacy-shared-key-000000000000000000";

    static readonly IPAddress PublicAddress = IPAddress.Parse("203.0.113.10");
    static readonly IPAddress PrivateAddress = IPAddress.Parse("172.18.0.5");

    [Theory]
    [InlineData("GET", "/valheim/zdo-redirect/pending/p7-primary-v1")]
    [InlineData("POST", "/valheim/zdo-redirect/ack/p7-primary-v1")]
    [InlineData("POST", "/valheim/zdo-redirect/consumer")]
    [InlineData("POST", "/valheim/telemetry/heartbeat")]
    [InlineData("GET", "/valheim/zdo-injection/next/p7-primary-v1")]
    [InlineData("POST", "/valheim/zdo-injection/ack")]
    public async Task EnrolledPublicCaller_ReachesConsumerSurfaces(string method, string path)
    {
        var (service, issued) = CreateEnrolledService();
        var context = Request(method, path, PublicAddress,
            ("X-Lumberjacks-Enrollment-Id", issued.Enrollment.EnrollmentId),
            ("X-Lumberjacks-Client-Key", issued.AccessToken));

        Assert.True(await Invoke(context, service));
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/valheim/zdo-redirect/receipts")]     // producer
    [InlineData("POST", "/valheim/zdo-redirect/reset/p7-primary-v1")]
    [InlineData("POST", "/valheim/zdo-redirect/reset")]
    [InlineData("POST", "/valheim/zdo-redirect/compact")]
    [InlineData("POST", "/valheim/handshake/peerinfo")]
    [InlineData("POST", "/valheim/handshake/reset")]
    [InlineData("POST", "/valheim/zdo-injection/stage")]
    [InlineData("POST", "/valheim/priority-manifests/x/activate")]
    [InlineData("GET", "/valheim/zdo-redirect/status")]
    [InlineData("POST", "/api/v0/enrollment/invites")]
    [InlineData("GET", "/api/v0/enrollment")]
    public async Task EnrolledPublicCaller_CannotReachProducerOrAdminSurfaces(string method, string path)
    {
        var (service, issued) = CreateEnrolledService();
        var context = Request(method, path, PublicAddress,
            ("X-Lumberjacks-Enrollment-Id", issued.Enrollment.EnrollmentId),
            ("X-Lumberjacks-Client-Key", issued.AccessToken));

        Assert.False(await Invoke(context, service));
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task SharedKeyPublicCaller_KeepsConsumerAccessButLosesAdmin()
    {
        var (service, _) = CreateEnrolledService();

        var consumer = Request("GET", "/valheim/zdo-redirect/pending/p7-primary-v1", PublicAddress,
            ("X-Lumberjacks-Client-Key", SharedKey));
        Assert.True(await Invoke(consumer, service));

        var admin = Request("POST", "/valheim/zdo-redirect/compact", PublicAddress,
            ("X-Lumberjacks-Client-Key", SharedKey));
        Assert.False(await Invoke(admin, service));
        Assert.Equal(StatusCodes.Status403Forbidden, admin.Response.StatusCode);
    }

    [Fact]
    public async Task AnonymousPublicCaller_IsRejectedWithReason()
    {
        var (service, _) = CreateEnrolledService();
        var context = Request("GET", "/valheim/zdo-redirect/pending/p7-primary-v1", PublicAddress);

        Assert.False(await Invoke(context, service));
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task RevokedEnrollment_IsRejected()
    {
        var (service, issued) = CreateEnrolledService();
        Assert.True(service.Revoke(issued.Enrollment.EnrollmentId, "test"));

        var context = Request("GET", "/valheim/zdo-redirect/pending/p7-primary-v1", PublicAddress,
            ("X-Lumberjacks-Enrollment-Id", issued.Enrollment.EnrollmentId),
            ("X-Lumberjacks-Client-Key", issued.AccessToken));

        Assert.False(await Invoke(context, service));
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/valheim/zdo-redirect/receipts")]
    [InlineData("POST", "/valheim/zdo-redirect/reset/p7-primary-v1")]
    [InlineData("POST", "/valheim/handshake/peerinfo")]
    [InlineData("POST", "/valheim/zdo-redirect/compact")]
    [InlineData("POST", "/api/v0/enrollment/invites")]
    public async Task PrivatePlaneCaller_RetainsFullAccess(string method, string path)
    {
        var (service, _) = CreateEnrolledService();
        var context = Request(method, path, PrivateAddress);

        Assert.True(await Invoke(context, service));
    }

    [Fact]
    public async Task UngatedPaths_PassThrough()
    {
        var (service, _) = CreateEnrolledService();
        var context = Request("GET", "/api/v0/telemetry/valheim", PublicAddress);

        Assert.True(await Invoke(context, service));
    }

    [Fact]
    public async Task EnrolledCaller_GetsPrincipalWithEnrollmentAttached()
    {
        var (service, issued) = CreateEnrolledService();
        var context = Request("GET", "/valheim/zdo-redirect/pending/p7-primary-v1", PublicAddress,
            ("X-Lumberjacks-Enrollment-Id", issued.Enrollment.EnrollmentId),
            ("X-Lumberjacks-Client-Key", issued.AccessToken));

        Assert.True(await Invoke(context, service));
        var principal = ValheimPrincipal.From(context);
        Assert.NotNull(principal);
        Assert.Equal("enrollment", principal!.Kind);
        Assert.Equal(issued.Enrollment.RecipientId, principal.Enrollment!.RecipientId);
    }

    (SteamEnrollmentService Service, SteamEnrollmentService.EnrollmentIssued Issued) CreateEnrolledService()
    {
        Directory.CreateDirectory(_directory);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LUMBERJACKS_ENROLLMENT_PATH"] = Path.Combine(_directory, "invites.json"),
        }).Build();
        var service = new SteamEnrollmentService(config);
        Assert.True(service.TryRedeem(service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var bootstrap, out _));
        // Redeeming only issues the browser's one-use code; the access token these
        // tests present as a client key exists only once the installer spends it.
        Assert.True(service.TryConsumeBootstrap(bootstrap.BootstrapToken, out var issued, out _));
        return (service, issued);
    }

    static DefaultHttpContext Request(string method, string path, IPAddress remote, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remote;
        context.Response.Body = new MemoryStream();
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        return context;
    }

    /// <summary>Runs the middleware; returns true when the pipeline continued.</summary>
    static async Task<bool> Invoke(HttpContext context, SteamEnrollmentService service)
    {
        var reachedNext = false;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LUMBERJACKS_CLIENT_ACCESS_KEY"] = SharedKey,
        }).Build();
        var middleware = new ValheimClientAccessMiddleware(_ => { reachedNext = true; return Task.CompletedTask; }, config);
        await middleware.InvokeAsync(context, service);
        return reachedNext;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
