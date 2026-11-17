using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Infrastructure.Integrations;

// Inactive adapter: rejection semantics are not yet trusted as proof of non-consumption.
internal sealed class SupabaseSignupRetryProvider(SupabaseAuthService provider) : IPersistentSignupRetryProvider
{
    public async Task<SignupProviderResult> VerifyAsync(string email, string code, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = await provider.VerifyOtpAsync(email, code, "signup");
        ct.ThrowIfCancellationRequested();
        if (session == null || !Guid.TryParseExact(session.User.Id, "D", out var subject) || subject == Guid.Empty)
            return new(SignupProviderOutcome.Unconfirmed);
        var verified = await provider.VerifySessionUserAsync(session, email, ct: ct);
        return new(SignupProviderOutcome.Confirmed, subject, verified.Email,
            verified.EmailConfirmedAt != null, !string.IsNullOrWhiteSpace(session.AccessToken));
    }

    public async Task<SignupProviderResult> ResendAsync(string email, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await provider.ResendSignupAsync(email);
        ct.ThrowIfCancellationRequested();
        return new(SignupProviderOutcome.Confirmed);
    }
}
