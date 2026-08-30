using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunitySurvival.Networking;

public sealed record NativeAccessConfig(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("gateway_url")] string GatewayUrl,
    [property: JsonPropertyName("enrollment_id")] string EnrollmentId,
    [property: JsonPropertyName("client_key")] string ClientKey,
    [property: JsonPropertyName("required_release")] string RequiredRelease)
{
    public const string SchemaId = "lumberjacks-native-access/v1";

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
        if (!string.Equals(access.RequiredRelease, SimulationClient.CurrentRelease,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"This pass expects Lumberjacks {access.RequiredRelease}; this build is {SimulationClient.CurrentRelease}.");
        return access;
    }
}
