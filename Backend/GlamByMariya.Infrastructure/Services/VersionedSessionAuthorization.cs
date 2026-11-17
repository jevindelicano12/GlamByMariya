using System.Globalization;
using System.Security.Claims;

namespace GlamByMariya.Infrastructure.Services;

// Inactive: both values must come from one authoritative database snapshot.
public sealed record VersionedAccountSecurity(AccountSecuritySnapshot Account, long SecurityVersion);

public interface IVersionedAccountSecurityProvider
{
    Task<VersionedAccountSecurity?> LoadAsync(Guid accountId, CancellationToken ct);
}

// Additive to the existing durable guard; cookie expiry remains the host's responsibility.
public sealed class VersionedSessionAuthorization(IVersionedAccountSecurityProvider provider)
{
    public const string SecurityVersionClaim = "security_version";

    public async Task<(AccountSessionEvidence Evidence, long SecurityVersion)?> PrepareSupabaseLogin(
        Guid accountId, Guid verifiedSubject, string expectedRole, CancellationToken ct = default)
    {
        var snapshot = await Load(accountId, ct);
        if (snapshot == null) return null;
        var evidence = await Guard(snapshot).PrepareSupabaseLogin(accountId, verifiedSubject, expectedRole, ct);
        return evidence == null ? null : (evidence, snapshot.SecurityVersion);
    }

    public async Task<bool> ValidateCookie(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        var ids = principal?.FindAll(ClaimTypes.NameIdentifier).ToArray() ?? [];
        var versions = principal?.FindAll(SecurityVersionClaim).ToArray() ?? [];
        if (ids.Length != 1 || versions.Length != 1 ||
            !Guid.TryParseExact(ids[0].Value, "D", out var id) || id == Guid.Empty || ids[0].Value != id.ToString() ||
            !long.TryParse(versions[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version <= 0 || versions[0].Value != version.ToString(CultureInfo.InvariantCulture)) return false;
        var snapshot = await Load(id, ct);
        return snapshot != null && snapshot.SecurityVersion == version && await Guard(snapshot).ValidateCookie(principal, ct);
    }

    private async Task<VersionedAccountSecurity?> Load(Guid accountId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await provider.LoadAsync(accountId, ct);
            ct.ThrowIfCancellationRequested();
            return snapshot?.Account != null && snapshot.SecurityVersion > 0 ? snapshot : null;
        }
        catch (Exception) { return null; }
    }

    private static DurableSessionAuthorization Guard(VersionedAccountSecurity snapshot) => new(new FixedSnapshot(snapshot.Account));

    private sealed class FixedSnapshot(AccountSecuritySnapshot account) : IAccountSecuritySnapshotProvider
    {
        public Task<AccountSecuritySnapshot?> LoadAsync(Guid accountId, CancellationToken ct) => Task.FromResult<AccountSecuritySnapshot?>(account);
    }
}
