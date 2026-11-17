using GlamByMariya.Infrastructure.Services;

var account = Guid.NewGuid(); var subject = Guid.NewGuid();
var store = new FakeStore(account, subject);
var a = new DurableCredentialUpdate(store); var b = new DurableCredentialUpdate(store);
void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
Task<CredentialProviderResult> Provider(CredentialProviderResult result) => Task.FromResult(result);

using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    var callsBefore = 0;
    Check(await a.Change(account, subject, 1, "synthetic", _ => { callsBefore++; return Provider(CredentialProviderResult.Confirmed); }, canceled.Token) == DurableCredentialResult.NotStarted
        && store.Epoch == 1 && store.State == "Ready" && callsBefore == 0,
        "already-canceled change does not begin or call provider even if store ignores cancellation");
}
foreach (var providerResult in new[] { CredentialProviderResult.Confirmed, CredentialProviderResult.DefinitivelyRejected })
{
    var canceledStore = new FakeStore(account, subject);
    using var canceled = new CancellationTokenSource();
    var canceledResult = await new DurableCredentialUpdate(canceledStore).Change(account, subject, 1, "synthetic", _ =>
    {
        canceled.Cancel();
        return Provider(providerResult);
    }, canceled.Token);
    Check(canceledResult == DurableCredentialResult.Unconfirmed && canceledStore.Epoch == 2
        && canceledStore.State == "ReconciliationRequired" && canceledStore.Password == "old",
        "cancellation after provider " + providerResult + " cannot complete or reject unresolved update");
}
{
    var canceledStore = new FakeStore(account, subject) { FailMark = true };
    using var canceled = new CancellationTokenSource();
    var canceledResult = await new DurableCredentialUpdate(canceledStore).Change(account, subject, 1, "synthetic", _ =>
    {
        canceled.Cancel();
        return Provider(CredentialProviderResult.Confirmed);
    }, canceled.Token);
    Check(canceledResult == DurableCredentialResult.Unconfirmed && canceledStore.Epoch == 2
        && canceledStore.State == "Pending" && canceledStore.Password == "old",
        "cancellation with failed reconciliation marker remains pending and cannot complete");
}
{
    var canceledStore = new FakeStore(account, subject);
    using var canceled = new CancellationTokenSource();
    canceledStore.CancelAfterComplete = canceled;
    var canceledResult = await new DurableCredentialUpdate(canceledStore).Change(account, subject, 1, "synthetic",
        _ => Provider(CredentialProviderResult.Confirmed), canceled.Token);
    Check(canceledResult == DurableCredentialResult.Unconfirmed && canceledStore.Epoch == 2 && canceledStore.State == "Ready",
        "cancellation during completion does not claim success even if commit happened");
}
Check(await a.Change(account, subject, 1, "synthetic", _ => Provider(CredentialProviderResult.Confirmed)) == DurableCredentialResult.Completed
    && store.Epoch == 2 && store.State == "Ready" && store.Password == "synthetic", "confirmed update advances epoch and synchronizes");
Check(await a.Change(account, subject, 2, "other", _ => Provider(CredentialProviderResult.DefinitivelyRejected)) == DurableCredentialResult.Rejected
    && store.Epoch == 3 && store.Password == "synthetic", "definite rejection retains advanced epoch without password change");
Check(await a.Change(account, subject, 3, "other", _ => throw new TimeoutException()) == DurableCredentialResult.Unconfirmed
    && store.Epoch == 4 && store.State == "ReconciliationRequired", "ambiguous timeout requires reconciliation");
{
    var delayedStore = new FakeStore(account, subject);
    var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var providerPassword = "old";
    Task? delayedWrite = null;
    var outcome = await new DurableCredentialUpdate(delayedStore).Change(account, subject, 1, "new", _ =>
    {
        delayedWrite = CommitLater();
        return Task.FromException<CredentialProviderResult>(new TimeoutException("synthetic"));
    });
    async Task CommitLater() { await releaseProvider.Task; providerPassword = "new"; }
    var retryCalls = 0;
    var retry = await new DurableCredentialUpdate(delayedStore).Change(account, subject, 2, "retry", _ =>
    { retryCalls++; return Provider(CredentialProviderResult.Confirmed); });
    releaseProvider.SetResult();
    await delayedWrite!;
    Check(outcome == DurableCredentialResult.Unconfirmed && retry == DurableCredentialResult.NotStarted && retryCalls == 0
        && providerPassword == "new" && delayedStore.State == "ReconciliationRequired" && delayedStore.Epoch == 2
        && delayedStore.Password == "old" && !await delayedStore.Complete(account, subject, 2, "new", default)
        && !await delayedStore.Reject(account, subject, 2, default),
        "late fake provider write after timeout cannot reopen or retry unresolved state");
}
var calls = 0;
Check(await b.Change(account, subject, 4, "other", _ => { calls++; return Provider(CredentialProviderResult.Confirmed); }) == DurableCredentialResult.NotStarted
    && calls == 0, "unresolved state blocks provider call");
store.State = "Ready";
Check(await a.Change(account, Guid.NewGuid(), 4, "other", _ => { calls++; return Provider(CredentialProviderResult.Confirmed); }) == DurableCredentialResult.NotStarted
    && calls == 0, "identity mismatch blocks provider call");
store.FailBegin = true;
Check(await a.Change(account, subject, 4, "other", _ => { calls++; return Provider(CredentialProviderResult.Confirmed); }) == DurableCredentialResult.BeginUnconfirmed
    && calls == 0, "database begin failure prevents provider call");
store.FailBegin = false; store.FailComplete = true;
Check(await a.Change(account, subject, 4, "other", _ => Provider(CredentialProviderResult.Confirmed)) == DurableCredentialResult.Unconfirmed
    && store.State == "Pending", "local sync failure remains pending and unconfirmed");
store.FailComplete = false; store.State = "Ready";
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var first = a.Change(account, subject, 5, "first", async _ => { started.SetResult(); await release.Task; return CredentialProviderResult.Confirmed; });
await started.Task;
var second = await b.Change(account, subject, 5, "second", _ => { calls++; return Provider(CredentialProviderResult.Confirmed); });
release.SetResult();
Check(second == DurableCredentialResult.NotStarted && await first == DurableCredentialResult.Completed && calls == 0,
    "two simulated instances allow only one concurrent update");
Check(!await store.Reject(account, subject, 5, default) && store.State == "Ready" && store.Epoch == 6,
    "stale operation token cannot change later state");
store.FailBeginAfterCommit = true;
Check(await a.Change(account, subject, 6, "other", _ => { calls++; return Provider(CredentialProviderResult.Confirmed); }) == DurableCredentialResult.BeginUnconfirmed
    && store.State == "Pending" && store.Epoch == 7 && calls == 0, "ambiguous begin commit never starts provider");
store.FailBeginAfterCommit = false; store.State = "Ready"; store.FailMark = true;
Check(await a.Change(account, subject, 7, "other", _ => Provider(CredentialProviderResult.Uncertain)) == DurableCredentialResult.Unconfirmed
    && store.State == "Pending", "failed reconciliation marker leaves pending");
store.FailMark = false; store.State = "Ready"; store.FailCompleteAfterCommit = true;
Check(await a.Change(account, subject, 8, "other", _ => Provider(CredentialProviderResult.Confirmed)) == DurableCredentialResult.Unconfirmed
    && store.State == "Ready", "ambiguous completion commit never reports success");
Console.WriteLine("Fake store only: no PostgreSQL, cookie middleware, real provider, or cross-process persistence tested.");

sealed class FakeStore(Guid account, Guid subject) : IDurableCredentialStore
{
    private readonly object gate = new();
    public long Epoch { get; private set; } = 1;
    public string State { get; set; } = "Ready";
    public string Password { get; private set; } = "old";
    public bool FailBegin { get; set; }
    public bool FailBeginAfterCommit { get; set; }
    public bool FailComplete { get; set; }
    public bool FailCompleteAfterCommit { get; set; }
    public CancellationTokenSource? CancelAfterComplete { get; set; }
    public bool FailMark { get; set; }
    public Task<long?> Begin(Guid id, Guid provider, long expected, CancellationToken ct)
    {
        lock (gate) { if (FailBegin) throw new IOException(); if (id != account || provider != subject || expected != Epoch || State != "Ready") return Task.FromResult<long?>(null); State = "Pending"; Epoch++; if (FailBeginAfterCommit) throw new IOException(); return Task.FromResult<long?>(Epoch); }
    }
    public Task<bool> Complete(Guid id, Guid provider, long epoch, string password, CancellationToken ct)
    {
        lock (gate) { if (FailComplete) throw new IOException(); if (!Matches(id, provider, epoch)) return Task.FromResult(false); Password = password; State = "Ready"; CancelAfterComplete?.Cancel(); if (FailCompleteAfterCommit) throw new IOException(); return Task.FromResult(true); }
    }
    public Task<bool> Reject(Guid id, Guid provider, long epoch, CancellationToken ct)
    { lock (gate) { if (!Matches(id, provider, epoch)) return Task.FromResult(false); State = "Ready"; return Task.FromResult(true); } }
    public Task<bool> MarkReconciliation(Guid id, Guid provider, long epoch, CancellationToken ct)
    { lock (gate) { if (FailMark) throw new IOException(); if (!Matches(id, provider, epoch)) return Task.FromResult(false); State = "ReconciliationRequired"; return Task.FromResult(true); } }
    private bool Matches(Guid id, Guid provider, long epoch) => id == account && provider == subject && epoch == Epoch && State == "Pending";
}
