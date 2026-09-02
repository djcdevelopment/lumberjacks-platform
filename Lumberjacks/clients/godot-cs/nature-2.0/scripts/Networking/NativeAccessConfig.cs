using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunitySurvival.Networking;

public sealed record NativeAccessConfig(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("gateway_url")] string GatewayUrl,
    [property: JsonPropertyName("enrollment_id")] string EnrollmentId,
    [property: JsonPropertyName("client_key")] string ClientKey,
    [property: JsonPropertyName("required_release")] string RequiredRelease,
    [property: JsonIgnore] bool PrivateRAndD = false,
    [property: JsonIgnore] string RAndDUsername = "",
    [property: JsonIgnore] string RAndDPassword = "")
{
    public const string SchemaId = "lumberjacks-native-access/v1";
    public const string CurrentRelease = "0.1.0-alpha.1";
    public const string DefaultRAndDUsername = "derek";
    public const string DefaultRAndDPassword = "lumberjacks-rnd";

    /// <summary>
    /// Temporary R&amp;D entry path. The deliberately non-secret defaults are a convenience
    /// check only; Gateway also requires the IAP/private network plane.
    /// </summary>
    public static NativeAccessConfig ForPrivateRAndD(string serverAddress)
    {
        var gateway = NormalizeGateway(serverAddress);
        var username = Environment.GetEnvironmentVariable("LUMBERJACKS_RND_USERNAME") ??
            DefaultRAndDUsername;
        var password = Environment.GetEnvironmentVariable("LUMBERJACKS_RND_PASSWORD") ??
            DefaultRAndDPassword;
        return new NativeAccessConfig(
            SchemaId, gateway, "", "", CurrentRelease,
            true, username, password);
    }

    public static NativeAccessConfig Parse(string json)
    {
        var access = JsonSerializer.Deserialize<NativeAccessConfig>(json) ??
            throw new InvalidOperationException("The field pass is empty.");
        if (!string.Equals(access.Schema, SchemaId, StringComparison.Ordinal))
            throw new InvalidOperationException("This is not a Lumberjacks field pass.");
        if (!Uri.TryCreate(access.GatewayUrl, UriKind.Absolute, out var gateway) ||
            (gateway.Scheme != "ws" && gateway.Scheme != "wss") ||
            gateway.AbsolutePath != "/game")
            throw new InvalidOperationException("The field pass has an invalid server address.");
        if (string.IsNullOrWhiteSpace(access.EnrollmentId) ||
            string.IsNullOrWhiteSpace(access.ClientKey))
            throw new InvalidOperationException("The field pass is missing its personal access credential.");
        if (!string.Equals(access.RequiredRelease, CurrentRelease,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"This pass expects Lumberjacks {access.RequiredRelease}; this build is {CurrentRelease}.");
        return access;
    }

    private static string NormalizeGateway(string value)
    {
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "ws://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var gateway) ||
            (gateway.Scheme != "ws" && gateway.Scheme != "wss"))
            throw new InvalidOperationException("Enter a ws:// or wss:// Lumberjacks server address.");

        var builder = new UriBuilder(gateway);
        if (builder.Path is "" or "/") builder.Path = "/game";
        if (!string.Equals(builder.Path, "/game", StringComparison.Ordinal))
            throw new InvalidOperationException("The Lumberjacks game endpoint must end in /game.");
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
