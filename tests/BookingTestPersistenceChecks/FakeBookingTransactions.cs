// Deliberately in-memory test double for illustrating expected replay/rollback
// semantics. It does not execute, emulate, or prove PostgreSQL behavior.
internal sealed class FakeBookingTransactions
{
    private readonly object gate = new();
    private readonly List<Row> rows = [];
    internal int Count { get { lock (gate) return rows.Count; } }
    internal Guid Create(Guid actor, Guid token, byte[] hash, DateTime start, DateTime reservedUntil, bool failBeforeCommit = false)
    {
        lock (gate)
        {
            var existing = rows.SingleOrDefault(x => x.Actor == actor && x.Token == token);
            if (existing != null)
            {
                if (!BookingTestCreateRepository.SameReplay(existing.Hash, hash)) throw new InvalidOperationException("Replay mismatch.");
                return existing.Id;
            }
            if (rows.Any(x => start < x.ReservedUntil && reservedUntil > x.Start)) throw new InvalidOperationException("Conflict.");
            var pending = new Row(Guid.NewGuid(), actor, token, hash.ToArray(), start, reservedUntil);
            if (failBeforeCommit) throw new InvalidOperationException("Simulated rollback.");
            rows.Add(pending);
            return pending.Id;
        }
    }
    private sealed record Row(Guid Id, Guid Actor, Guid Token, byte[] Hash, DateTime Start, DateTime ReservedUntil);
}
