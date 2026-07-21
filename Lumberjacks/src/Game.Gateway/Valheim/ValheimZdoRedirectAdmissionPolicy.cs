namespace Game.Gateway.Valheim;

public sealed record ValheimZdoRedirectAdmissionResult(
    bool Allowed,
    int StatusCode,
    string? Error,
    string? AdmittedRelease,
    bool LegacyUnadmitted);

/// <summary>
/// Release admission for the real ZDO receipt path. Schema 1 remains the explicit rollback
/// contract; a schema-2 submission must name and match the release baked into this Gateway — unless
/// this Gateway bakes no release at all (an uncut dev build), in which case it is admitted
/// unattested rather than refused, mirroring the handshake's fail-open on a null baked id.
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
        // An uncut Gateway build bakes no release id (ValheimReleaseIdentity.ExpectedModRelease is
        // null). Admit unattested rather than refusing every schema-2 submission with a 503: the
        // handshake already skips its own release gate on a null baked id
        // (ValheimHandshakeService.cs J-gate), so a dev build that connects cleanly can also submit,
        // instead of connecting and then having every ZDO rejected. LegacyUnadmitted records that
        // the receipt was not attested against a release. A cut release always bakes a real id, so
        // null cannot occur in a promoted image — this admits nothing in production that a
        // configured Gateway would refuse. (HANDOFF task 2 / DECISIONS-PENDING option (a).)
        if (string.IsNullOrWhiteSpace(expectedModRelease))
            return new(true, StatusCodes.Status200OK, null, null, LegacyUnadmitted: true);
        if (string.IsNullOrWhiteSpace(presentedModRelease))
            return new(false, StatusCodes.Status409Conflict, "mod_release_required",
                expectedModRelease, LegacyUnadmitted: false);
        if (!string.Equals(presentedModRelease, expectedModRelease, StringComparison.Ordinal))
            return new(false, StatusCodes.Status409Conflict, "mod_release_incompatible",
                expectedModRelease, LegacyUnadmitted: false);
        return new(true, StatusCodes.Status200OK, null, expectedModRelease, LegacyUnadmitted: false);
    }
}
