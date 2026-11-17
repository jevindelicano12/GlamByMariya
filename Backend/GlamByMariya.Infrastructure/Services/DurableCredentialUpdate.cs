namespace GlamByMariya.Infrastructure.Services;

// Inactive contract: implementations must use one authoritative primary and commit
// each transition before returning; Begin must verify fresh proof and identity.
public interface IDurableCredentialStore
{
    Task<long?> Begin(Guid accountId, Guid providerSubject, long expectedEpoch, CancellationToken ct);
    // This must atomically verify Pending/epoch/identity, sync the local credential,
    // and set Ready; never change the account's role or active status.
    Task<bool> Complete(Guid accountId, Guid providerSubject, long epoch, string newPassword, CancellationToken ct);
    Task<bool> Reject(Guid accountId, Guid providerSubject, long epoch, CancellationToken ct);
    Task<bool> MarkReconciliation(Guid accountId, Guid providerSubject, long epoch, CancellationToken ct);
}

public enum CredentialProviderResult { Uncertain, Confirmed, DefinitivelyRejected }
public enum DurableCredentialResult { NotStarted, BeginUnconfirmed, Completed, Rejected, Unconfirmed }

// Not registered or called by the application. A provider result is confirmed
// only when its adapter can prove the outcome and subject; no automatic retry.
public sealed class DurableCredentialUpdate(IDurableCredentialStore store)
{
    public async Task<DurableCredentialResult> Change(Guid accountId, Guid subject, long expectedEpoch,
        string newPassword, Func<CancellationToken, Task<CredentialProviderResult>> update, CancellationToken ct = default)
    {
        if (accountId == Guid.Empty || subject == Guid.Empty || expectedEpoch < 1 || expectedEpoch == long.MaxValue ||
            string.IsNullOrEmpty(newPassword) || update == null || ct.IsCancellationRequested) return DurableCredentialResult.NotStarted;

        long? epoch;
        try { epoch = await store.Begin(accountId, subject, expectedEpoch, ct); }
        catch (Exception) { return DurableCredentialResult.BeginUnconfirmed; }
        // A malformed or uncertain begin result never authorizes a provider call.
        if (epoch == null) return DurableCredentialResult.NotStarted;
        if (epoch != expectedEpoch + 1 || ct.IsCancellationRequested) return DurableCredentialResult.BeginUnconfirmed;

        CredentialProviderResult result;
        try { result = await update(ct); }
        catch (Exception) { result = CredentialProviderResult.Uncertain; }
        if (ct.IsCancellationRequested || result == CredentialProviderResult.Uncertain || !Enum.IsDefined(result))
        {
            try { await store.MarkReconciliation(accountId, subject, epoch.Value, ct); } catch (Exception) { }
            return DurableCredentialResult.Unconfirmed;
        }
        try
        {
            var committed = result == CredentialProviderResult.Confirmed
                ? await store.Complete(accountId, subject, epoch.Value, newPassword, ct)
                : await store.Reject(accountId, subject, epoch.Value, ct);
            if (!committed || ct.IsCancellationRequested) return DurableCredentialResult.Unconfirmed;
            return result == CredentialProviderResult.Confirmed ? DurableCredentialResult.Completed : DurableCredentialResult.Rejected;
        }
        catch (Exception) { return DurableCredentialResult.Unconfirmed; }
    }
}
