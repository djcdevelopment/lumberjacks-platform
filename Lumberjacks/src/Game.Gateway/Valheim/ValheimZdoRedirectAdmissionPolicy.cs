namespace Game.Gateway.Valheim;

public sealed record ValheimZdoRedirectAdmissionResult(
    bool Allowed,
    int StatusCode,
    string? Error,
    string? AdmittedRelease,
    bool LegacyUnadmitted);

/// <summary>
/// Release admission for the real ZDO receipt path. Schema 1 remains the explicit rollback
/// contract; every schema-2 submission must name and match the release baked into this Gateway.
/// </summary>
public static class ValheimZdoRedirectAdmissionPolicy
{
    public const int CurrentSchemaVersion = 2;
    public const string Operation = "zdo_redirect";

    public static ValheimZdoRedirectAdmissionResult Evaluate(
        int? schemaVersion,
        string? presentedModRelease,
        string? expectedModRelease)
    {
        var schema = schemaVersion.GetValueOrDefault(1);
        if (schema == 1)
            return new(true, StatusCodes.Status200OK, null, expectedModRelease, LegacyUnadmitted: true);
        if (schema != CurrentSchemaVersion)
            return new(false, StatusCodes.Status400BadRequest, "schema_version_unsupported",
                expectedModRelease, LegacyUnadmitted: false);
        if (string.IsNullOrWhiteSpace(expectedModRelease))
            return new(false, StatusCodes.Status503ServiceUnavailable,
                "release_admission_unconfigured", null, LegacyUnadmitted: false);
        if (string.IsNullOrWhiteSpace(presentedModRelease))
            return new(false, StatusCodes.Status409Conflict, "mod_release_required",
                expectedModRelease, LegacyUnadmitted: false);
        if (!string.Equals(presentedModRelease, expectedModRelease, StringComparison.Ordinal))
            return new(false, StatusCodes.Status409Conflict, "mod_release_incompatible",
                expectedModRelease, LegacyUnadmitted: false);
        return new(true, StatusCodes.Status200OK, null, expectedModRelease, LegacyUnadmitted: false);
    }
}
