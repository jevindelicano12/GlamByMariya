using GlamByMariya.Infrastructure.Data;

var id = Guid.NewGuid(); var fake = new FakeCommand(id); var repo = new AccountSecurityGenerationRepository(fake);
var count = 0;
void Check(bool ok, string text) { if (!ok) throw new Exception(text); count++; Console.WriteLine("PASS " + text); }
Check(!await repo.AdvanceInTransactionAsync(Guid.Empty, 1) && !await repo.AdvanceInTransactionAsync(id, 0)
    && !await repo.AdvanceInTransactionAsync(id, long.MaxValue) && fake.Calls == 0, "invalid and overflow inputs do not execute");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    Check(!await repo.AdvanceInTransactionAsync(id, 1, cancel.Token) && fake.Calls == 0, "pre-cancellation does not execute");
}
Check(await repo.AdvanceInTransactionAsync(id, 1) && fake.Generation == 2, "matching generation advances");
Check(!await repo.AdvanceInTransactionAsync(id, 1) && fake.Generation == 2, "stale generation denied");
Check(!await repo.AdvanceInTransactionAsync(Guid.NewGuid(), 2) && fake.Generation == 2, "unknown account denied");
var competing = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => repo.AdvanceInTransactionAsync(id, 2))));
Check(competing.Count(x => x) == 1 && fake.Generation == 3, "concurrent fake updates have one winner");
fake.ResultOverride = 9;
try { await repo.AdvanceInTransactionAsync(id, 3); throw new Exception("malformed result accepted"); }
catch (InvalidOperationException) { Check(true, "malformed database result fails closed"); }
fake.ResultOverride = null; fake.Fail = true;
try { await repo.AdvanceInTransactionAsync(id, 3); throw new Exception("failure accepted"); }
catch (IOException) { Check(true, "database failure is not reported as success"); }
fake.Fail = false;
using (var cancel = new CancellationTokenSource())
{
    fake.CancelAfterAdvance = cancel;
    try { await repo.AdvanceInTransactionAsync(id, 3, cancel.Token); throw new Exception("cancellation accepted"); }
    catch (OperationCanceledException) { Check(true, "post-command cancellation does not report success"); }
}
var sql = PostgresAccountSecurityGenerationCommand.Sql;
Check(sql.Contains("@account_id") && sql.Contains("@expected") && sql.Contains("generation = generation + 1")
    && sql.Contains("RETURNING generation"), "SQL is parameterized and conditional");
Console.WriteLine($"{count} passed; fake command only, no PostgreSQL, authorization, transaction commit or crash tested.");

sealed class FakeCommand(Guid id) : IAccountSecurityGenerationCommand
{
    private readonly object gate = new();
    public long Generation { get; private set; } = 1;
    public int Calls { get; private set; }
    public bool Fail { get; set; }
    public long? ResultOverride { get; set; }
    public CancellationTokenSource? CancelAfterAdvance { get; set; }
    public Task<long?> AdvanceAsync(Guid accountId, long expectedGeneration, CancellationToken ct)
    {
        lock (gate)
        {
            Calls++;
            if (Fail) throw new IOException("synthetic");
            if (ResultOverride.HasValue) return Task.FromResult(ResultOverride);
            if (id != accountId || Generation != expectedGeneration) return Task.FromResult<long?>(null);
            Generation++;
            CancelAfterAdvance?.Cancel();
            return Task.FromResult<long?>(Generation);
        }
    }
}
