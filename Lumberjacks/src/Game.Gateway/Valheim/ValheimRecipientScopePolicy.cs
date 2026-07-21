namespace Game.Gateway.Valheim;

/// <summary>Stable delivery subjects used by the Gateway queue.</summary>
public static class ValheimRecipient
{
    public const string Legacy = "legacy";
}

/// <summary>
/// Resolves the recipient for consumer operations without trusting a caller label.
/// </summary>
public static class ValheimRecipientScopePolicy
{
    public static (string? Resolved, string? Error) Resolve(
        string? principalKind, string? recipientId, string? requestedRecipient,
        bool producerEmitsRecipients)
    {
        if (string.Equals(principalKind, "enrollment", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(recipientId))
                return (null, "enrollment recipient_id is required");
            if (!producerEmitsRecipients)
                return (ValheimRecipient.Legacy, null);
            return (recipientId.Trim(), null);
        }

        if (string.Equals(principalKind, "private-plane", StringComparison.Ordinal) ||
            string.Equals(principalKind, "shared-client-key", StringComparison.Ordinal))
            return (ValheimRecipient.Legacy, null);

        return (null, "consumer principal is required");
    }
}
