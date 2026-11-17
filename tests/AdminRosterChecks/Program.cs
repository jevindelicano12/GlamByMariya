using System.Security.Claims;
using System.Text.Json;
using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;

var owner = Guid.NewGuid();
var admin = Guid.NewGuid();
var subject = Guid.NewGuid();
var adminSubject = Guid.NewGuid();
const string hash = "synthetic-roster-hash";
var passed = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
Dictionary<string, object?> Payload(Guid id, Guid sub, string role = "Admin", bool active = true, bool locked = false) => new()
{
    ["metadata_available"] = true,
    ["accounts"] = new[] { new { id, kind = "Admin", role, active, locked, password_hash = hash } },
    ["identities"] = new[] { new { id, customer = (Guid?)null, admin = (Guid?)id, auth_state = "Supabase", subject = sub, state = "Ready", epoch = 1L } },
    ["login_integrity"] = true, ["subject_unique"] = true, ["generations"] = new long[] { 1 }
};
ClaimsPrincipal Ticket(long generation = 1, string role = "SuperAdmin") => new(new ClaimsIdentity(
    [new(ClaimTypes.NameIdentifier, owner.ToString()), new(ClaimTypes.Role, role),
     new(DurableSessionAuthorization.EpochClaim, "1"), new("account_version", AccountSession.Version(hash)),
     new(DurableSessionAuthorization.SubjectClaim, subject.ToString()),
     new(VersionedSessionAuthorization.SecurityVersionClaim, generation.ToString())], "test"));
var factory = new FakeFactory();
void Reset()
{
    factory.Rows = new() { [owner] = JsonSerializer.Serialize(Payload(owner, subject, "SuperAdmin")), [admin] = JsonSerializer.Serialize(Payload(admin, adminSubject)) };
    factory.Targets = [new(admin, "artist", "artist@example.invalid")];
    factory.Fail = false;
    factory.OnTargets = null;
}
var service = new AdminRosterService(factory);
Reset();
var roster = await service.ReadAsync(Ticket());
Check(roster is { Count: 1 } && roster[0] == new AdminRosterEntry(admin, "artist", "artist@example.invalid", true, false, 1),
    "current SuperAdmin receives safe Admin fields and generation");
Check(!JsonSerializer.Serialize(roster).Contains(hash), "result never contains password hash");
Check(await service.ReadAsync(Ticket(2)) == null, "stale SuperAdmin generation denied");
Check(await service.ReadAsync(Ticket(role: "Admin")) == null, "non-SuperAdmin ticket denied");
Check(await service.ReadAsync(new ClaimsPrincipal()) == null, "unauthenticated principal denied");
Reset(); factory.Targets.Clear();
Check((await service.ReadAsync(Ticket())) is { Count: 0 }, "empty roster is valid");
Reset(); factory.Targets = Enumerable.Range(0, AdminRosterService.MaximumAccounts + 1)
    .Select(_ => new AdminRosterCandidate(Guid.NewGuid(), "artist", "artist@example.invalid")).ToList();
Check(await service.ReadAsync(Ticket()) == null, "oversized roster fails closed");
Reset();
var p = Payload(owner, subject, "SuperAdmin", active: false); factory.Rows[owner] = JsonSerializer.Serialize(p);
Check(await service.ReadAsync(Ticket()) == null, "disabled SuperAdmin denied");
p = Payload(owner, subject, "SuperAdmin", locked: true); factory.Rows[owner] = JsonSerializer.Serialize(p);
Check(await service.ReadAsync(Ticket()) == null, "locked SuperAdmin denied");
Reset(); p = Payload(owner, subject, "SuperAdmin"); p["generations"] = new long[] { 1, 2 }; factory.Rows[owner] = JsonSerializer.Serialize(p);
Check(await service.ReadAsync(Ticket()) == null, "duplicate actor generations denied");
Reset(); p = Payload(admin, adminSubject); p["generations"] = new long[] { 1, 2 }; factory.Rows[admin] = JsonSerializer.Serialize(p);
Check(await service.ReadAsync(Ticket()) == null, "duplicate target generations denied");
Reset(); p = Payload(admin, adminSubject); p["generations"] = Array.Empty<long>(); factory.Rows[admin] = JsonSerializer.Serialize(p);
Check(await service.ReadAsync(Ticket()) == null, "missing target generation denied");
Reset(); p = Payload(admin, adminSubject); p["login_integrity"] = false; factory.Rows[admin] = JsonSerializer.Serialize(p);
Check(await service.ReadAsync(Ticket()) == null, "inconsistent target identity denies entire roster");
Reset(); factory.Rows[admin] = JsonSerializer.Serialize(Payload(admin, adminSubject, "SuperAdmin"));
Check(await service.ReadAsync(Ticket()) == null, "SuperAdmin target never returned");
Reset(); factory.Rows[admin] = JsonSerializer.Serialize(Payload(admin, adminSubject, active: false, locked: true));
roster = await service.ReadAsync(Ticket());
Check(roster is { Count: 1 } && !roster[0].IsActive && roster[0].IsLocked, "inactive and locked status retained");
Reset(); factory.Targets.Add(factory.Targets[0]);
Check(await service.ReadAsync(Ticket()) == null, "duplicate target IDs denied");
Reset(); factory.Targets[0] = new(admin, "bad\nname", "artist@example.invalid");
Check(await service.ReadAsync(Ticket()) == null, "unsafe display data denied");
Reset(); factory.Rows.Remove(admin);
Check(await service.ReadAsync(Ticket()) == null, "missing target denied");
Reset(); factory.Fail = true;
Check(await service.ReadAsync(Ticket()) == null, "database failure denied without cached result");
Reset(); using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    Check(await service.ReadAsync(Ticket(), canceled.Token) == null, "canceled request denied");
}
Reset();
factory.OnTargets = () =>
{
    var changed = Payload(admin, adminSubject, active: false);
    changed["generations"] = new long[] { 2 };
    factory.Rows[admin] = JsonSerializer.Serialize(changed);
};
roster = await service.ReadAsync(Ticket());
Check(roster is { Count: 1 } && roster[0].SecurityGeneration == 1 && roster[0].IsActive,
    "concurrent change does not mix fake snapshot states");
factory.OnTargets = null;
roster = await service.ReadAsync(Ticket());
Check(roster is { Count: 1 } && roster[0].SecurityGeneration == 2 && !roster[0].IsActive,
    "later snapshot sees concurrent change");
Reset(); p = Payload(owner, subject, "SuperAdmin"); p["generations"] = new long[] { 2 }; factory.Rows[owner] = JsonSerializer.Serialize(p);
Check(await service.ReadAsync(Ticket()) == null, "actor revoked before snapshot denied");
Console.WriteLine($"{passed} passed; fake snapshot and fake query only, no PostgreSQL or application host.");

sealed class FakeFactory : IAdminRosterReadSessionFactory
{
    public Dictionary<Guid, string> Rows { get; set; } = new();
    public List<AdminRosterCandidate> Targets { get; set; } = [];
    public bool Fail { get; set; }
    public Action? OnTargets { get; set; }
    public Task<IAdminRosterReadSession> OpenAsync(CancellationToken ct)
    {
        if (Fail) throw new IOException("synthetic");
        return Task.FromResult<IAdminRosterReadSession>(new Session(new(Rows), Targets.ToList(), OnTargets));
    }
    private sealed class Session(Dictionary<Guid, string> rows, List<AdminRosterCandidate> targets, Action? onTargets) : IAdminRosterReadSession
    {
        public Task<VersionedAccountSecurity?> LoadSecurityAsync(Guid id, CancellationToken ct) =>
            new PostgresVersionedAccountSecurityProvider(new Query(rows), "public").LoadAsync(id, ct);
        public Task<IReadOnlyList<AdminRosterCandidate>> ReadTargetsAsync(CancellationToken ct)
        {
            onTargets?.Invoke();
            return Task.FromResult<IReadOnlyList<AdminRosterCandidate>>(targets);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Query(Dictionary<Guid, string> rows) : IPostgresAccountSecurityQuery
    {
        public Task<string> ExecuteAsync(string sql, Guid id, CancellationToken ct) =>
            rows.TryGetValue(id, out var json) ? Task.FromResult(json) : throw new IOException("synthetic");
    }
}
