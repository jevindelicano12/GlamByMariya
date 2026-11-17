using System.Security.Claims;
using GlamByMariya.Core.BusinessRules;

namespace GlamByMariya.Infrastructure.Services;

public sealed record AdminRosterEntry(Guid Id, string Username, string Email, bool IsActive, bool IsLocked, long SecurityGeneration);
public sealed record AdminRosterCandidate(Guid Id, string? Username, string? Email);

// All reads from one session must share one authoritative, read-only snapshot.
public interface IAdminRosterReadSession : IAsyncDisposable
{
    Task<VersionedAccountSecurity?> LoadSecurityAsync(Guid accountId, CancellationToken ct);
    Task<IReadOnlyList<AdminRosterCandidate>> ReadTargetsAsync(CancellationToken ct);
}

public interface IAdminRosterReadSessionFactory
{
    Task<IAdminRosterReadSession> OpenAsync(CancellationToken ct);
}

// A roster is informational. Every later mutation must revalidate in its own transaction.
public sealed class AdminRosterService(IAdminRosterReadSessionFactory factory)
{
    public const int MaximumAccounts = 100;

    public async Task<IReadOnlyList<AdminRosterEntry>?> ReadAsync(ClaimsPrincipal? actor, CancellationToken ct = default)
    {
        var ids = actor?.FindAll(ClaimTypes.NameIdentifier).ToArray() ?? [];
        if (ids.Length != 1 || !Guid.TryParseExact(ids[0].Value, "D", out var actorId) ||
            actorId == Guid.Empty || ids[0].Value != actorId.ToString()) return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            await using var session = await factory.OpenAsync(ct);
            var owner = await session.LoadSecurityAsync(actorId, ct);
            if (owner?.Account.Role != "SuperAdmin" ||
                !await new VersionedSessionAuthorization(new Fixed(owner)).ValidateCookie(actor, ct)) return null;

            var targets = await session.ReadTargetsAsync(ct);
            if (targets == null || targets.Count > MaximumAccounts ||
                targets.Any(x => x == null || x.Id == Guid.Empty || x.Id == actorId ||
                    !Safe(x.Username, 100) || !Safe(x.Email, 254)) ||
                targets.Select(x => x.Id).Distinct().Count() != targets.Count) return null;

            var result = new List<AdminRosterEntry>(targets.Count);
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                var security = await session.LoadSecurityAsync(target.Id, ct);
                if (security?.Account is not { Role: "Admin", IsActive: bool active, IsLocked: bool locked,
                    SupabaseSubject: Guid subject } account || subject == Guid.Empty ||
                    account.AccountId != target.Id || security.SecurityVersion <= 0) return null;
                // Disabled or locked targets remain visible, but their identity must still be consistent.
                var normalized = account with { IsActive = true, IsLocked = false };
                if (await new DurableSessionAuthorization(new FixedAccount(normalized))
                    .PrepareSupabaseLogin(target.Id, subject, "Admin", ct) == null) return null;
                result.Add(new(target.Id, target.Username!, target.Email!, active, locked, security.SecurityVersion));
            }
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) { return null; }
    }

    private static bool Safe(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    private sealed class Fixed(VersionedAccountSecurity value) : IVersionedAccountSecurityProvider
    {
        public Task<VersionedAccountSecurity?> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult<VersionedAccountSecurity?>(value);
    }

    private sealed class FixedAccount(AccountSecuritySnapshot value) : IAccountSecuritySnapshotProvider
    {
        public Task<AccountSecuritySnapshot?> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult<AccountSecuritySnapshot?>(value);
    }
}
