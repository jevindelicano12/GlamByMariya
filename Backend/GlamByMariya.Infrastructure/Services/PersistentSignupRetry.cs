namespace GlamByMariya.Infrastructure.Services;

// Candidate only: no application registration or provider adapter is wired to this contract.
public enum SignupOperation { Verify, Resend }
public enum SignupProviderOutcome { Confirmed, DefinitivelyRejected, Unconfirmed }
public enum SignupRetryResult { Unavailable, TryAgain, ResendAccepted, Completed, Unconfirmed }

public sealed record SignupOperationClaim(Guid OperationId, string Email, Guid Subject);
public sealed record SignupProviderResult(SignupProviderOutcome Outcome, Guid Subject = default,
    string? Email = null, bool EmailConfirmed = false, bool HasAccessToken = false);

// Each method must use the browser-secret hash and a current, unexpired, unique
// operation claim. Begin serializes resend/verify and enforces resend cooldown.
// Commit atomically consumes the attempt and creates the account, binding and
// initial non-reused security generation; false is not proof of rollback.
public interface IPersistentSignupRetryStore
{
    Task<SignupOperationClaim?> BeginAsync(Guid attemptId, string browserSecret, SignupOperation operation, CancellationToken ct);
    Task<bool> ReleaseAsync(Guid attemptId, string browserSecret, Guid operationId, CancellationToken ct);
    Task<bool> CommitAsync(Guid attemptId, string browserSecret, SignupOperationClaim claim, CancellationToken ct);
}

// DefinitivelyRejected is permitted only after provider-specific semantics have
// been independently verified; unknown responses must be Unconfirmed.
public interface IPersistentSignupRetryProvider
{
    Task<SignupProviderResult> VerifyAsync(string email, string code, CancellationToken ct);
    Task<SignupProviderResult> ResendAsync(string email, CancellationToken ct);
}

public sealed class PersistentSignupRetry(IPersistentSignupRetryStore store, IPersistentSignupRetryProvider provider)
{
    public Task<SignupRetryResult> VerifyAsync(Guid attemptId, string browserSecret, string code, CancellationToken ct = default) =>
        RunAsync(attemptId, browserSecret, SignupOperation.Verify, code, ct);

    public Task<SignupRetryResult> ResendAsync(Guid attemptId, string browserSecret, CancellationToken ct = default) =>
        RunAsync(attemptId, browserSecret, SignupOperation.Resend, null, ct);

    private async Task<SignupRetryResult> RunAsync(Guid attemptId, string browserSecret, SignupOperation operation, string? code, CancellationToken ct)
    {
        if (browserSecret == null) return SignupRetryResult.Unavailable;
        byte[] secret;
        try { secret = Convert.FromBase64String(browserSecret); }
        catch (FormatException) { return SignupRetryResult.Unavailable; }
        if (attemptId == Guid.Empty || secret.Length != 32 || ct.IsCancellationRequested ||
            (operation == SignupOperation.Verify &&
             (code == null || code.Length != 6 || code.Any(c => c is < '0' or > '9'))))
            return SignupRetryResult.Unavailable;

        try
        {
            var claim = await store.BeginAsync(attemptId, browserSecret, operation, ct);
            if (claim == null) return SignupRetryResult.Unavailable;
            if (claim.OperationId == Guid.Empty || claim.Subject == Guid.Empty || string.IsNullOrWhiteSpace(claim.Email))
                return SignupRetryResult.Unconfirmed;

            var result = operation == SignupOperation.Verify
                ? await provider.VerifyAsync(claim.Email, code!, ct)
                : await provider.ResendAsync(claim.Email, ct);
            if (result.Outcome == SignupProviderOutcome.Unconfirmed) return SignupRetryResult.Unconfirmed;
            if (result.Outcome == SignupProviderOutcome.DefinitivelyRejected)
                return await store.ReleaseAsync(attemptId, browserSecret, claim.OperationId, ct)
                    ? SignupRetryResult.TryAgain : SignupRetryResult.Unconfirmed;
            if (result.Outcome != SignupProviderOutcome.Confirmed) return SignupRetryResult.Unconfirmed;
            if (operation == SignupOperation.Resend)
                return await store.ReleaseAsync(attemptId, browserSecret, claim.OperationId, ct)
                    ? SignupRetryResult.ResendAccepted : SignupRetryResult.Unconfirmed;
            if (!result.EmailConfirmed || !result.HasAccessToken || result.Subject != claim.Subject ||
                string.IsNullOrWhiteSpace(result.Email) ||
                !string.Equals(result.Email.Trim(), claim.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                return SignupRetryResult.Unconfirmed;
            return await store.CommitAsync(attemptId, browserSecret, claim, ct)
                ? SignupRetryResult.Completed : SignupRetryResult.Unconfirmed;
        }
        catch (Exception)
        {
            // Never retry a possibly completed provider or database operation.
            return SignupRetryResult.Unconfirmed;
        }
    }
}
