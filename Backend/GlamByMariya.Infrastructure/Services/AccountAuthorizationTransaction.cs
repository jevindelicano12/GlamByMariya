using System.Security.Claims;
using GlamByMariya.Infrastructure.Data;

namespace GlamByMariya.Infrastructure.Services;

public enum AccountAuthorizationAction { Disable, Lock, Reactivate, RevokeSessions }

// Implementations must lock both accounts in a deterministic order on the primary,
// return one authoritative snapshot per account, and roll back unless committed.
public interface IAccountAuthorizationTransaction : IAccountSecurityGenerationCommand, IAsyncDisposable
{
    Task<(VersionedAccountSecurity? Actor, VersionedAccountSecurity? Target)> LockAccountsAsync(Guid actor, Guid target, CancellationToken ct);
    Task<bool> ApplyAsync(Guid target, AccountAuthorizationAction action, DateTimeOffset? lockUntil, CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
}

public interface IAccountAuthorizationTransactionFactory
{
    Task<IAccountAuthorizationTransaction> BeginAsync(CancellationToken ct);
}

// Inactive preparation only. The principal must be a host-validated protected cookie;
// this service additionally revalidates it against locked authoritative snapshots.
public sealed class AccountAuthorizationTransactionService(IAccountAuthorizationTransactionFactory factory, TimeProvider clock)
{
    public async Task<bool> ChangeAsync(ClaimsPrincipal actor, Guid targetId, long expectedGeneration,
        AccountAuthorizationAction action, DateTimeOffset? lockUntil = null, CancellationToken ct = default)
    {
        var ids = actor?.FindAll(ClaimTypes.NameIdentifier).ToArray() ?? [];
        if (ids.Length != 1 || !Guid.TryParseExact(ids[0].Value, "D", out var actorId) || actorId == Guid.Empty ||
            ids[0].Value != actorId.ToString() || targetId == Guid.Empty || actorId == targetId ||
            expectedGeneration <= 0 || expectedGeneration == long.MaxValue || !Enum.IsDefined(action) || ct.IsCancellationRequested ||
            (action == AccountAuthorizationAction.Lock ? !lockUntil.HasValue || lockUntil <= clock.GetUtcNow() : lockUntil.HasValue)) return false;
        try
        {
            await using var tx = await factory.BeginAsync(ct);
            var (source, target) = await tx.LockAccountsAsync(actorId, targetId, ct);
            if (source == null || target == null || source.Account.AccountId != actorId || target.Account.AccountId != targetId ||
                source.Account.Role != "SuperAdmin" || target.Account.Role != "Admin" || target.SecurityVersion != expectedGeneration ||
                !await new VersionedSessionAuthorization(new Fixed(source)).ValidateCookie(actor, ct) || !await TargetIdentityValid(target, ct)) return false;
            if (action == AccountAuthorizationAction.Lock && lockUntil <= clock.GetUtcNow()) return false;
            if ((action == AccountAuthorizationAction.Disable && target.Account.IsActive != true) ||
                (action == AccountAuthorizationAction.Lock && (target.Account.IsActive != true || target.Account.IsLocked != false)) ||
                (action == AccountAuthorizationAction.Reactivate && (target.Account.IsActive != false || target.Account.IsLocked != false))) return false;
            if (!await new AccountSecurityGenerationRepository(tx).AdvanceInTransactionAsync(targetId, expectedGeneration, ct) ||
                !await tx.ApplyAsync(targetId, action, lockUntil, ct)) return false;
            ct.ThrowIfCancellationRequested();
            await tx.CommitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception) { return false; }
    }

    private static async Task<bool> TargetIdentityValid(VersionedAccountSecurity target, CancellationToken ct)
    {
        var a = target.Account;
        if (a.IsActive == null || a.IsLocked == null || a.SupabaseSubject is not Guid subject || subject == Guid.Empty) return false;
        // Inactive or locked targets may be managed, but identity/credential metadata must remain valid.
        var normalized = a with { IsActive = true, IsLocked = false };
        return await new DurableSessionAuthorization(new FixedAccount(normalized)).PrepareSupabaseLogin(a.AccountId, subject, "Admin", ct) != null;
    }

    private sealed class Fixed(VersionedAccountSecurity value) : IVersionedAccountSecurityProvider
    {
        public Task<VersionedAccountSecurity?> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult<VersionedAccountSecurity?>(value);
    }
    private sealed class FixedAccount(AccountSecuritySnapshot value) : IAccountSecuritySnapshotProvider
    {
        public Task<AccountSecuritySnapshot?> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult<AccountSecuritySnapshot?>(value);
    }
}
