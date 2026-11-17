using System.Globalization;
using System.Security.Claims;

namespace GlamByMariya.Infrastructure.Services;

// Inactive adapter: the caller must obtain fresh, verified provider proof before
// requesting a login ticket. Never construct the subject from untrusted input.
public sealed class DurableSessionAuthorization(IAccountSecuritySnapshotProvider provider)
{
    public const string EpochClaim = "credential_epoch";
    public const string SubjectClaim = "provider_subject";

    public async Task<AccountSessionEvidence?> PrepareSupabaseLogin(Guid accountId, Guid verifiedSubject, string expectedRole, CancellationToken ct = default)
    {
        if (accountId == Guid.Empty || verifiedSubject == Guid.Empty || string.IsNullOrEmpty(expectedRole)) return null;
        AccountSecuritySnapshot? snapshot;
        try { ct.ThrowIfCancellationRequested(); snapshot = await provider.LoadAsync(accountId, ct); ct.ThrowIfCancellationRequested(); }
        catch (Exception) { return null; }
        if (snapshot == null || snapshot.Authentication != SessionAuthenticationKind.Supabase || snapshot.SupabaseSubject != verifiedSubject || snapshot.CredentialEpoch == null) return null;
        var evidence = new AccountSessionEvidence(accountId, expectedRole, snapshot.CredentialEpoch.Value.ToString(CultureInfo.InvariantCulture), snapshot.AccountVersion);
        // Guard this exact snapshot; cookie validation must reload on every request.
        var decision = await new AccountSessionSecurityGuard(new Snapshot(snapshot)).EvaluateAsync(evidence, ct);
        return decision.IsEligible ? evidence : null;
    }

    public async Task<bool> ValidateCookie(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        if (principal?.Identity?.IsAuthenticated != true) return false;
        var ids = principal.FindAll(ClaimTypes.NameIdentifier).ToArray();
        var roles = principal.FindAll(ClaimTypes.Role).ToArray();
        var epochs = principal.FindAll(EpochClaim).ToArray();
        var versions = principal.FindAll("account_version").ToArray();
        var subjects = principal.FindAll(SubjectClaim).ToArray();
        if (ids.Length != 1 || roles.Length != 1 || epochs.Length != 1 || versions.Length != 1 || subjects.Length != 1 ||
            !Guid.TryParseExact(subjects[0].Value, "D", out var subject) || subject == Guid.Empty || subjects[0].Value != subject.ToString() ||
            !Guid.TryParseExact(ids[0].Value, "D", out var id) || id == Guid.Empty ||
            !string.Equals(id.ToString(), ids[0].Value, StringComparison.Ordinal)) return false;
        AccountSecuritySnapshot? snapshot;
        try { ct.ThrowIfCancellationRequested(); snapshot = await provider.LoadAsync(id, ct); ct.ThrowIfCancellationRequested(); }
        catch (Exception) { return false; }
        if (snapshot == null || snapshot.Authentication != SessionAuthenticationKind.Supabase || snapshot.SupabaseSubject != subject) return false;
        return (await new AccountSessionSecurityGuard(new Snapshot(snapshot)).EvaluateAsync(new(id, roles[0].Value, epochs[0].Value, versions[0].Value), ct)).IsEligible;
    }

    private sealed class Snapshot(AccountSecuritySnapshot value) : IAccountSecuritySnapshotProvider
    {
        public Task<AccountSecuritySnapshot?> LoadAsync(Guid accountId, CancellationToken ct) => Task.FromResult<AccountSecuritySnapshot?>(value);
    }
}
