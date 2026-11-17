using System.Collections.Immutable;

namespace GlamByMariya.Infrastructure.Services;

// Read-side seam only; no PostgreSQL implementation or DI registration exists.
// Read by immutable account UUID, across BOTH account tables, in one consistent
// database snapshot. Return all identity candidates whose UUID OR owner matches.
// Global uniqueness facts must be established in that same read, not guessed
// from a filtered list. Never repair rows, link by email, or default missing state.
public interface IAccountSecurityReadSource
{
    Task<AccountSecurityRead> ReadAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed record AccountSecurityAccountRow(
    Guid AccountId, bool? IsActive, bool? IsLocked, string? Role,
    SessionIdentityOwnerKind OwnerKind, string? AccountVersion);

// Owner IDs here are resolved app UUIDs, not the physical bigint foreign keys.
public sealed record AccountSecurityIdentityRow(
    Guid IdentityId, Guid? CustomerAccountId, Guid? AdminAccountId,
    string? AuthState, Guid? SupabaseSubject, string? CredentialState, long? CredentialEpoch);

public sealed record AccountSecurityRead(
    ImmutableArray<AccountSecurityAccountRow> Accounts,
    ImmutableArray<AccountSecurityIdentityRow> Identities,
    bool? NormalizedLoginConsistentAndUnique,
    bool? SupabaseSubjectUnique);

public enum AccountSecurityReadFailure { InvalidRequest, InvalidAccountMapping, RepositoryUnavailable }

public sealed class AccountSecurityReadException(AccountSecurityReadFailure failure)
    : Exception("Account security read denied.")
{
    public AccountSecurityReadFailure Failure { get; } = failure;
}

// Actual adapter, testable with a fake read source. Configuration must come from
// trusted application settings, never from request/session claims. This adapter
// supplies metadata; ONLY the existing guard decides session eligibility.
public sealed class RepositoryAccountSecuritySnapshotProvider(
    IAccountSecurityReadSource source, SessionStoreKind store, SessionAuthenticationKind authentication)
    : IAccountSecuritySnapshotProvider
{
    private readonly IAccountSecurityReadSource source = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<AccountSecuritySnapshot?> LoadAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
            throw new AccountSecurityReadException(AccountSecurityReadFailure.InvalidRequest);

        AccountSecurityRead read;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            read = await source.ReadAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            // Deliberately exclude inner exceptions: provider messages can carry
            // SQL, connection details or other sensitive data. No stale fallback.
            throw new AccountSecurityReadException(AccountSecurityReadFailure.RepositoryUnavailable);
        }

        if (read == null || read.Accounts.IsDefault || read.Identities.IsDefault
            || read.Accounts.Any(row => row == null) || read.Identities.Any(row => row == null))
            throw new AccountSecurityReadException(AccountSecurityReadFailure.RepositoryUnavailable);

        if (read.Accounts.Length == 0 && read.Identities.Length == 0) return null;
        if (read.Accounts.Length != 1 || read.Accounts[0].AccountId != accountId)
            throw new AccountSecurityReadException(AccountSecurityReadFailure.InvalidAccountMapping);

        var account = read.Accounts[0];
        // No first-row-wins behavior: multiple candidates remain inconsistent.
        var identity = read.Identities.Length == 1 ? read.Identities[0] : null;
        var customerOwner = identity?.CustomerAccountId;
        var adminOwner = identity?.AdminAccountId;
        var exactlyOneOwner = (customerOwner.HasValue ? 1 : 0) + (adminOwner.HasValue ? 1 : 0) == 1;
        var ownerKind = exactlyOneOwner
            ? customerOwner.HasValue ? SessionIdentityOwnerKind.Customer : SessionIdentityOwnerKind.Admin
            : SessionIdentityOwnerKind.Unknown;
        var ownerId = exactlyOneOwner ? customerOwner ?? adminOwner : null;
        var expectedOwnerKind = account.Role switch
        {
            "Customer" => SessionIdentityOwnerKind.Customer,
            "Admin" or "SuperAdmin" => SessionIdentityOwnerKind.Admin,
            _ => SessionIdentityOwnerKind.Unknown
        };
        var consistent = identity != null && identity.IdentityId == accountId && ownerId == accountId
            && ownerKind != SessionIdentityOwnerKind.Unknown && ownerKind == account.OwnerKind
            && ownerKind == expectedOwnerKind
            && read.NormalizedLoginConsistentAndUnique == true && read.SupabaseSubjectUnique == true;

        return new AccountSecuritySnapshot(accountId, account.IsActive, account.IsLocked, account.Role,
            store, authentication, read.Identities.Length,
            // Duplicate candidates retain only an observed ID for diagnostics;
            // no candidate's security state or provider link is selected.
            // Count/integrity still force the guard to deny this snapshot.
            read.Identities.Length > 0 ? read.Identities[0].IdentityId : null,
            ownerId, ownerKind, consistent, identity?.AuthState, identity?.SupabaseSubject,
            ParseState(identity?.CredentialState), identity?.CredentialEpoch, account.AccountVersion);
    }

    private static SessionCredentialState ParseState(string? state) => state switch
    {
        "Ready" => SessionCredentialState.Ready,
        "Pending" => SessionCredentialState.Pending,
        "ReconciliationRequired" => SessionCredentialState.ReconciliationRequired,
        _ => SessionCredentialState.Unknown
    };
}
