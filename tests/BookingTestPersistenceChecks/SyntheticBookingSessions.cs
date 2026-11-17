using System.Security.Cryptography;

// In-process test host only; never registered in MVC. Credentials are delivered
// only to the trusted harness during fixture setup, not selected by requests.
// This does not create public accounts or prove a database identity.
internal sealed class SyntheticBookingSessions
{
    internal static readonly Guid CustomerA = Guid.Parse("10000000-0000-4000-8000-000000000001");
    internal static readonly Guid CustomerB = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<string, Guid> credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> disabled = [];
    private readonly SemaphoreSlim customerAGate = new(1, 1);
    private readonly SemaphoreSlim customerBGate = new(1, 1);

    internal SyntheticBookingSessions(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    // Trusted fixture setup only. No caller-provided Customer ID is accepted at login.
    internal string ProvisionCredential(Guid fixtureId)
    {
        if (fixtureId != CustomerA && fixtureId != CustomerB) throw new InvalidOperationException("Not a synthetic fixture identity.");
        lock (gate)
        {
            if (disabled.Contains(fixtureId) || credentials.ContainsValue(fixtureId)) throw new InvalidOperationException("Fixture identity unavailable.");
            var secret = NewToken();
            credentials.Add(Digest(secret), fixtureId);
            return secret;
        }
    }

    internal string SignIn(string? credential)
    {
        lock (gate)
        {
            if (!ValidToken(credential) || !credentials.TryGetValue(Digest(credential!), out var customer) || disabled.Contains(customer))
                throw new InvalidOperationException("Synthetic sign-in denied.");
            var token = NewToken();
            sessions.Add(Digest(token), new(customer, clock.GetUtcNow().AddMinutes(10)));
            return token;
        }
    }

    internal Guid Authenticate(string? token)
    {
        lock (gate)
        {
            if (!ValidToken(token) || !sessions.TryGetValue(Digest(token!), out var session) ||
                session.ExpiresAt <= clock.GetUtcNow() || disabled.Contains(session.CustomerId))
                throw new InvalidOperationException("Synthetic session invalid or expired.");
            return session.CustomerId;
        }
    }

    private DateTimeOffset Expiration(string token)
    {
        lock (gate)
        {
            _ = Authenticate(token);
            return sessions[Digest(token)].ExpiresAt;
        }
    }

    internal VerifiedCustomer Verify(string? token) => VerifiedCustomer.FromSession(this, token);

    // Constructor is private: even test callers must present a live session.
    // Revalidation prevents a previously issued object surviving revocation.
    internal sealed class VerifiedCustomer
    {
        private readonly SyntheticBookingSessions issuer;
        private readonly string token;
        private VerifiedCustomer(SyntheticBookingSessions issuer, string token) { this.issuer = issuer; this.token = token; }
        internal static VerifiedCustomer FromSession(SyntheticBookingSessions issuer, string? token)
        {
            _ = issuer.Authenticate(token);
            return new VerifiedCustomer(issuer, token!);
        }
        internal Guid CustomerId => issuer.Authenticate(token);
        internal DateTimeOffset ExpiresAt => issuer.Expiration(token);
        // Internal test-host transport only; caller must promptly clear the copy.
        internal byte[] CopyProof() { _ = CustomerId; return Convert.FromHexString(token); }
        internal async Task<IDisposable> AcquireOperationAsync(CancellationToken ct = default)
        {
            var customer = issuer.Authenticate(token);
            var operationGate = issuer.OperationGate(customer);
            await operationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (issuer.Authenticate(token) != customer) throw new InvalidOperationException("Synthetic identity changed.");
                return new OperationLease(operationGate);
            }
            catch { operationGate.Release(); throw; }
        }
    }

    internal void Disable(Guid fixtureId) => DisableAsync(fixtureId).GetAwaiter().GetResult();

    internal async Task DisableAsync(Guid fixtureId, CancellationToken ct = default)
    {
        var operationGate = OperationGate(fixtureId);
        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                disabled.Add(fixtureId);
                foreach (var key in sessions.Where(x => x.Value.CustomerId == fixtureId).Select(x => x.Key).ToArray()) sessions.Remove(key);
                foreach (var key in credentials.Where(x => x.Value == fixtureId).Select(x => x.Key).ToArray()) credentials.Remove(key);
            }
        }
        finally { operationGate.Release(); }
    }

    private SemaphoreSlim OperationGate(Guid customer) => customer == CustomerA ? customerAGate : customer == CustomerB ? customerBGate : throw new InvalidOperationException("Not a synthetic fixture identity.");
    private sealed class OperationLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? held = gate;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static bool ValidToken(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(value)));
    private sealed record Session(Guid CustomerId, DateTimeOffset ExpiresAt);
}
