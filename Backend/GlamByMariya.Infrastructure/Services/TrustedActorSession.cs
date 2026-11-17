using System.Globalization;
using System.Security.Claims;
using GlamByMariya.Core.BusinessRules;

namespace GlamByMariya.Infrastructure.Services;

/// <summary>
/// Evidence copied from an ASP.NET authenticated, protected session. This type
/// is not a provider or cookie verifier: IsAuthenticated and these claims can
/// be fabricated in process. Only a verified host authentication flow may supply
/// the principal, and the inactive database candidate cannot prove that occurred.
/// </summary>
public sealed record TrustedActorSession(
    Guid AccountId,
    string Role,
    Guid ProviderSubject,
    long CredentialEpoch,
    long SecurityGeneration)
{
    public static bool TryReadProtectedPrincipal(ClaimsPrincipal? principal, out TrustedActorSession? session)
    {
        session = null;
        if (principal?.Identity?.IsAuthenticated != true) return false;

        var ids = principal.FindAll(ClaimTypes.NameIdentifier).ToArray();
        var roles = principal.FindAll(ClaimTypes.Role).ToArray();
        var subjects = principal.FindAll(DurableSessionAuthorization.SubjectClaim).ToArray();
        var epochs = principal.FindAll(DurableSessionAuthorization.EpochClaim).ToArray();
        var generations = principal.FindAll(VersionedSessionAuthorization.SecurityVersionClaim).ToArray();
        if (ids.Length != 1 || roles.Length != 1 || subjects.Length != 1 || epochs.Length != 1 || generations.Length != 1)
            return false;

        if (!TryCanonicalGuid(ids[0].Value, out var accountId)
            || !TryCanonicalGuid(subjects[0].Value, out var subject)
            || !TryCanonicalPositiveLong(epochs[0].Value, out var epoch)
            || !TryCanonicalPositiveLong(generations[0].Value, out var generation)
            || roles[0].Value is not ("Customer" or "Admin" or "SuperAdmin"))
            return false;

        session = new(accountId, roles[0].Value, subject, epoch, generation);
        return true;
    }

    private static bool TryCanonicalGuid(string? value, out Guid id)
    {
        id = Guid.Empty;
        return Guid.TryParseExact(value, "D", out id) && id != Guid.Empty
            && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);
    }

    private static bool TryCanonicalPositiveLong(string? value, out long result)
    {
        result = 0;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result > 0 && string.Equals(value, result.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
