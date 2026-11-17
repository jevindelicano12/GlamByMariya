namespace GlamByMariya.Infrastructure.Integrations;

/// <summary>Result of one Admin Auth provisioning request; does not imply local linkage or activation.</summary>
public sealed record SupabaseProvisioningResult
{
    private SupabaseProvisioningResult(SupabaseProvisioningOutcome outcome, Guid? providerUserId,
        SupabaseProvisioningFailure failure)
    {
        Outcome = outcome;
        ProviderUserId = providerUserId;
        Failure = failure;
    }

    public SupabaseProvisioningOutcome Outcome { get; }
    public Guid? ProviderUserId { get; }
    public SupabaseProvisioningFailure Failure { get; }
    public bool IsConfirmed => Outcome == SupabaseProvisioningOutcome.Confirmed
        && ProviderUserId is { } id && id != Guid.Empty
        && Failure == SupabaseProvisioningFailure.None;

    public static SupabaseProvisioningResult Confirmed(Guid userId) =>
        userId == Guid.Empty
            ? Unconfirmed(SupabaseProvisioningOutcome.Unconfirmed, SupabaseProvisioningFailure.InvalidResponse)
            : new(SupabaseProvisioningOutcome.Confirmed, userId, SupabaseProvisioningFailure.None);

    public static SupabaseProvisioningResult Unconfirmed(
        SupabaseProvisioningOutcome outcome, SupabaseProvisioningFailure failure) =>
        new(outcome, null, failure);
}

public enum SupabaseProvisioningOutcome
{
    Confirmed,
    NotAttempted,
    Unconfirmed,
    TransportUncertain,
    TimedOut,
    Canceled
}

public enum SupabaseProvisioningFailure
{
    None,
    MissingConfiguration,
    HttpRejected,
    TransportFailure,
    Timeout,
    Canceled,
    InvalidResponse
}
