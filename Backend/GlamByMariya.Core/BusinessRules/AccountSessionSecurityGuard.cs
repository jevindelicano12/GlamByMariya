using System.Globalization;

namespace GlamByMariya.Core.BusinessRules;

// Standalone Phase 1.14 component: deliberately not registered or wired to cookies.
// The provider must load one authoritative, consistent snapshot by immutable ID.
// It must not substitute default values for unavailable database metadata.
public interface IAccountSecuritySnapshotProvider
{
    Task<AccountSecuritySnapshot?> LoadAsync(Guid accountId, CancellationToken cancellationToken);
}

public enum SessionStoreKind { Unknown, PublicPostgres, LegacyPostgres, Demo }
public enum SessionAuthenticationKind { Unknown, LocalPassword, Supabase }
public enum SessionIdentityOwnerKind { Unknown, Customer, Admin }
public enum SessionCredentialState { Unknown, Ready, Pending, ReconciliationRequired }

// Nullable/Unknown values deliberately represent missing metadata, never eligibility.
// IdentityMappingConsistent includes normalized-login uniqueness and cross-table
// ownership checks that only an authoritative repository can establish.
public sealed record AccountSecuritySnapshot(
    Guid AccountId,
    bool? IsActive,
    bool? IsLocked,
    string? Role,
    SessionStoreKind Store,
    SessionAuthenticationKind Authentication,
    int? IdentityCount,
    Guid? IdentityId,
    Guid? IdentityOwnerAccountId,
    SessionIdentityOwnerKind IdentityOwnerKind,
    bool? IdentityMappingConsistent,
    string? AuthState,
    Guid? SupabaseSubject,
    SessionCredentialState CredentialState,
    long? CredentialEpoch,
    string? AccountVersion);

// Evidence from the session being evaluated, not a claim of fresh password proof.
// This guard neither authenticates passwords nor authorizes reconciliation.
public sealed record AccountSessionEvidence(
    Guid AccountId, string? Role, string? CredentialEpoch, string? AccountVersion);

public enum AccountSessionEligibility
{
    Unknown,
    Eligible,
    AccountMissing,
    AccountInactive,
    AccountLocked,
    InvalidSecurityMetadata,
    IdentityMissing,
    IdentityInconsistent,
    UnsupportedProviderAuthentication,
    CredentialNotReady,
    CredentialEpochMissing,
    CredentialEpochInvalid,
    CredentialEpochMismatch,
    AccountIdMismatch,
    RoleMismatch,
    AccountVersionMismatch,
    RepositoryUnavailable
}

public readonly record struct AccountSessionDecision(AccountSessionEligibility Outcome)
{
    public bool IsEligible => Outcome == AccountSessionEligibility.Eligible;
}

public sealed class AccountSessionSecurityGuard(IAccountSecuritySnapshotProvider provider)
{
    private readonly IAccountSecuritySnapshotProvider provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public async Task<AccountSessionDecision> EvaluateAsync(
        AccountSessionEvidence? evidence, CancellationToken cancellationToken = default)
    {
        if (evidence == null || evidence.AccountId == Guid.Empty)
            return Deny(AccountSessionEligibility.AccountIdMismatch);

        AccountSecuritySnapshot? snapshot;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = await provider.LoadAsync(evidence.AccountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            // No provider exception text or security values escape via this result.
            // Cancellation also denies; callers may handle request cancellation separately.
            return Deny(AccountSessionEligibility.RepositoryUnavailable);
        }

        if (snapshot == null) return Deny(AccountSessionEligibility.AccountMissing);
        if (snapshot.AccountId == Guid.Empty || snapshot.AccountId != evidence.AccountId)
            return Deny(AccountSessionEligibility.AccountIdMismatch);
        if (snapshot.IsActive == false) return Deny(AccountSessionEligibility.AccountInactive);
        if (snapshot.IsLocked == true) return Deny(AccountSessionEligibility.AccountLocked);
        if (snapshot.IsActive != true || snapshot.IsLocked != false)
            return Deny(AccountSessionEligibility.InvalidSecurityMetadata);

        // Initial durable scope is public PostgreSQL only. Local-only adapters for
        // legacy/Demo require a separate explicit policy, not synthetic Ready values.
        if (snapshot.Store != SessionStoreKind.PublicPostgres
            || snapshot.Authentication is not (SessionAuthenticationKind.LocalPassword or SessionAuthenticationKind.Supabase))
            return Deny(AccountSessionEligibility.UnsupportedProviderAuthentication);

        if (snapshot.IdentityCount == 0 || snapshot.IdentityId == null)
            return Deny(AccountSessionEligibility.IdentityMissing);
        if (snapshot.IdentityCount != 1 || snapshot.IdentityId != snapshot.AccountId
            || snapshot.IdentityOwnerAccountId != snapshot.AccountId || snapshot.IdentityMappingConsistent != true)
            return Deny(AccountSessionEligibility.IdentityInconsistent);

        if (snapshot.Role is not ("Customer" or "Admin" or "SuperAdmin"))
            return Deny(AccountSessionEligibility.InvalidSecurityMetadata);
        var expectedOwner = snapshot.Role == "Customer" ? SessionIdentityOwnerKind.Customer : SessionIdentityOwnerKind.Admin;
        if (snapshot.IdentityOwnerKind != expectedOwner)
            return Deny(AccountSessionEligibility.IdentityInconsistent);
        if (!string.Equals(snapshot.Role, evidence.Role, StringComparison.Ordinal))
            return Deny(AccountSessionEligibility.RoleMismatch);

        if (snapshot.Authentication == SessionAuthenticationKind.Supabase)
        {
            if (snapshot.AuthState != AuthenticationIdentityPolicy.Supabase
                || snapshot.SupabaseSubject == null || snapshot.SupabaseSubject == Guid.Empty)
                return Deny(AccountSessionEligibility.IdentityInconsistent);
        }
        else if (snapshot.AuthState != AuthenticationIdentityPolicy.LegacyOnly || snapshot.SupabaseSubject != null)
        {
            // Unknown is not implicit legacy permission; linked accounts cannot
            // fall back just because remote authentication is unconfigured.
            return Deny(AccountSessionEligibility.IdentityInconsistent);
        }

        if (snapshot.CredentialState == SessionCredentialState.Unknown
            || !Enum.IsDefined(snapshot.CredentialState))
            return Deny(AccountSessionEligibility.InvalidSecurityMetadata);
        if (snapshot.CredentialState != SessionCredentialState.Ready)
            return Deny(AccountSessionEligibility.CredentialNotReady);

        if (snapshot.CredentialEpoch == null || evidence.CredentialEpoch == null)
            return Deny(AccountSessionEligibility.CredentialEpochMissing);
        if (snapshot.CredentialEpoch <= 0
            || !long.TryParse(evidence.CredentialEpoch, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || epoch <= 0)
            return Deny(AccountSessionEligibility.CredentialEpochInvalid);
        if (snapshot.CredentialEpoch != epoch)
            return Deny(AccountSessionEligibility.CredentialEpochMismatch);

        if (!IsVersion(snapshot.AccountVersion) || !IsVersion(evidence.AccountVersion))
            return Deny(AccountSessionEligibility.InvalidSecurityMetadata);
        if (!string.Equals(snapshot.AccountVersion, evidence.AccountVersion, StringComparison.Ordinal))
            return Deny(AccountSessionEligibility.AccountVersionMismatch);

        return new(AccountSessionEligibility.Eligible);
    }

    private static bool IsVersion(string? version) => version is { Length: 64 }
        && version.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static AccountSessionDecision Deny(AccountSessionEligibility outcome) => new(outcome);
}
