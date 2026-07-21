using System.Reflection;

namespace Game.Gateway.Valheim;

/// <summary>
/// The mod release this Gateway build admits, read from the assembly it is compiled into.
///
/// Baked, not configured, and that is the whole point (M1 risk 9). An expected-release value that
/// an operator supplies — via an environment file, or via a runtime <c>POST /config</c>, which is
/// how this was first and wrongly built — is a second source of truth that can disagree with the
/// artifact that actually shipped. Its failure mode is not a noisy one: the gate calls an
/// incompatible release compatible, which is precisely what identity-pinning exists to prevent.
/// Compiled in, the expected value travels with the image and cannot drift from it.
///
/// The release cut sets it from the same id the manifest records:
/// <c>dotnet build -p:LumberjacksExpectedModRelease=m1-clean-20260717-r2</c>.
/// </summary>
public static class ValheimReleaseIdentity
{
    /// <summary>The MSBuild property name, mirrored in Game.Gateway.csproj.</summary>
    private const string MetadataKey = "LumberjacksExpectedModRelease";

    /// <summary>Sentinel for an uncut local build.</summary>
    private const string Uncut = "dev";

    /// <summary>
    /// The release id this build expects, or null when it has none — an uncut local build, or an
    /// assembly built before this was baked in. Null disables the release gate rather than
    /// rejecting everyone: a dev Gateway that refuses every join teaches people to switch the flag
    /// off and leave it off, which costs more than the gate is worth.
    /// </summary>
    public static readonly string? ExpectedModRelease = ReadBakedValue();

    private static string? ReadBakedValue()
    {
        var value = typeof(ValheimReleaseIdentity).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal))
            ?.Value;

        return string.IsNullOrWhiteSpace(value) || string.Equals(value, Uncut, StringComparison.Ordinal)
            ? null
            : value;
    }
}
