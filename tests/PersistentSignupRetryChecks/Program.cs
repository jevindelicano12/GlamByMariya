using System.Security.Cryptography;
using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Infrastructure.Integrations;
using GlamByMariya.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

if (args.Length != 0)
{
    if (args.Length != 1 || args[0] != "--disposable-postgres") throw new InvalidOperationException("Unknown test mode.");
    await PostgresRetryIntegration.RunAsync();
    return;
}

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}

var id = Guid.NewGuid();
var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
FakeStore NewStore() => new(id, secret);
var store = NewStore();
var provider = new FakeProvider();
var workflow = new PersistentSignupRetry(store, provider);
var fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "PersistentSignupRetryChecks", "disposable-fixture.sql");
var fixture = File.ReadAllText(fixturePath);
Check(fixture.Contains("current_database() <> 'glam_retry_disposable_' || marker", StringComparison.Ordinal) &&
    fixture.Contains("inet_server_addr() IS DISTINCT FROM '127.0.0.1'", StringComparison.Ordinal) &&
    fixture.Contains("Unsafe or nonempty disposable fixture target", StringComparison.Ordinal),
    "fixture source requires a named, empty loopback database");
Check(fixture.Contains("NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS", StringComparison.Ordinal) &&
    fixture.Contains("REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC", StringComparison.Ordinal) &&
    !fixture.Contains("GRANT ALL", StringComparison.OrdinalIgnoreCase),
    "fixture source provisions a restricted role without broad grants");
await using (var neverOpen = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1"))
{
    var actualStore = new PostgresPersistentSignupRetryStore(neverOpen, new EphemeralDataProtectionProvider());
    Check(await actualStore.BeginAsync(Guid.Empty, secret, SignupOperation.Verify, CancellationToken.None) == null,
        "PostgreSQL store rejects empty attempt before opening a connection");
    Check(!await actualStore.ReleaseAsync(id, "invalid", Guid.NewGuid(), CancellationToken.None),
        "PostgreSQL store rejects invalid ownership before opening a connection");
    Check(!await actualStore.CommitAsync(id, secret, new SignupOperationClaim(Guid.Empty, "customer@example.test", Guid.NewGuid()), CancellationToken.None),
        "PostgreSQL store rejects invalid operation before opening a connection");
    Check(!await actualStore.ReconcileCommittedAsync(id, secret, new SignupOperationClaim(Guid.Empty, "customer@example.test", Guid.NewGuid()), CancellationToken.None),
        "PostgreSQL reconciliation rejects invalid operation before opening a connection");
    var attempts = new PostgresDurableSignupAttempts(neverOpen, new EphemeralDataProtectionProvider());
    try { await attempts.Create(" ", "{}"); throw new Exception("Invalid input accepted."); }
    catch (ArgumentException) { Check(true, "PostgreSQL attempt creator rejects missing email before opening a connection"); }
    Check(PostgresDurableSignupAttempts.CreateSql.Contains("NOT EXISTS", StringComparison.Ordinal) &&
        PostgresDurableSignupAttempts.CreateSql.Contains("'Verifying','Resending','Unconfirmed'", StringComparison.Ordinal) &&
        PostgresDurableSignupAttempts.CreateSql.Contains("public.account_identities", StringComparison.Ordinal),
        "attempt creation SQL excludes unresolved attempts and existing identities (source contract only)");
}
Check(await workflow.VerifyAsync(id, secret, "12345x") == SignupRetryResult.Unavailable && store.BeginCalls == 0,
    "malformed OTP never claims or contacts provider");
Check(await workflow.VerifyAsync(id, "invalid", "123456") == SignupRetryResult.Unavailable && store.BeginCalls == 0,
    "malformed browser secret is rejected");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    Check(await workflow.VerifyAsync(id, secret, "123456", cancelled.Token) == SignupRetryResult.Unavailable && store.BeginCalls == 0,
        "pre-cancelled request starts no operation");
}
Check(await workflow.VerifyAsync(id, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "123456") == SignupRetryResult.Unavailable && provider.Calls == 0,
    "another browser cannot claim an attempt");
store.Expired = true;
Check(await workflow.ResendAsync(id, secret) == SignupRetryResult.Unavailable && provider.Calls == 0, "expired attempt cannot resend");
store.Expired = false;
store.Cooldown = true;
Check(await workflow.ResendAsync(id, secret) == SignupRetryResult.Unavailable && provider.Calls == 0, "resend cooldown denies provider request");
store.Cooldown = false;
var staleClaim = await store.BeginAsync(id, secret, SignupOperation.Resend, CancellationToken.None);
Check(staleClaim != null && !await store.ReleaseAsync(id, secret, Guid.NewGuid(), CancellationToken.None) && !store.Ready,
    "stale operation cannot release another claim");
Check(await store.ReleaseAsync(id, secret, staleClaim!.OperationId, CancellationToken.None), "own operation can release the claim");
provider.Outcome = SignupProviderOutcome.DefinitivelyRejected;
Check(await workflow.VerifyAsync(id, secret, "123456") == SignupRetryResult.TryAgain && store.Ready, "definitive rejection permits retry");
provider.Outcome = SignupProviderOutcome.Confirmed;
Check(await workflow.ResendAsync(id, secret) == SignupRetryResult.ResendAccepted && store.Ready, "accepted resend restores pending state");
provider.Subject = store.Subject;
Check(await workflow.VerifyAsync(id, secret, "123456") == SignupRetryResult.Completed && store.Account && store.Generation == 1,
    "matching provider proof commits account and initial generation");
var calls = provider.Calls;
Check(await workflow.VerifyAsync(id, secret, "123456") == SignupRetryResult.Unavailable && provider.Calls == calls && store.Generation == 1,
    "consumed attempt cannot replay or reset generation");

foreach (var defect in new[] { "subject", "email", "unconfirmed-email", "missing-token", "unknown", "timeout" })
{
    var current = NewStore();
    var remote = new FakeProvider { Subject = current.Subject, Defect = defect };
    var candidate = new PersistentSignupRetry(current, remote);
    Check(await candidate.VerifyAsync(id, secret, "123456") == SignupRetryResult.Unconfirmed && !current.Account && !current.Ready,
        defect + " remains unconfirmed without activation");
    Check(await candidate.ResendAsync(id, secret) == SignupRetryResult.Unavailable && remote.Calls == 1,
        defect + " cannot automatically retry");
}

{
    var current = NewStore();
    var remote = new FakeProvider { Subject = current.Subject };
    var candidate = new PersistentSignupRetry(current, remote);
    remote.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = candidate.VerifyAsync(id, secret, "123456");
    await remote.Started.Task;
    var competing = await Task.WhenAll(candidate.VerifyAsync(id, secret, "123456"), candidate.ResendAsync(id, secret));
    Check(competing.All(x => x == SignupRetryResult.Unavailable) && remote.Calls == 1, "concurrent verify and resend have one provider operation");
    remote.Block.SetResult();
    Check(await first == SignupRetryResult.Completed && current.Generation == 1, "winning operation commits once");
}

foreach (var fault in new[] { "before", "between", "after" })
{
    var current = NewStore();
    current.CommitFault = fault;
    var remote = new FakeProvider { Subject = current.Subject };
    var candidate = new PersistentSignupRetry(current, remote);
    Check(await candidate.VerifyAsync(id, secret, "123456") == SignupRetryResult.Unconfirmed, fault + " commit failure is never reported as success");
    Check(fault == "after" ? current.Account && current.Generation == 1 : !current.Account && current.Generation == 0,
        fault + " leaves account and generation atomically absent or committed");
    Check(await candidate.VerifyAsync(id, secret, "123456") == SignupRetryResult.Unavailable && remote.Calls == 1,
        fault + " commit ambiguity does not repeat provider verification");
}
{
    var current = NewStore();
    current.CommitFault = "after";
    current.ConfirmCommittedAfterFault = true;
    var remote = new FakeProvider { Subject = current.Subject };
    var candidate = new PersistentSignupRetry(current, remote);
    Check(await candidate.VerifyAsync(id, secret, "123456") == SignupRetryResult.Completed && current.Account && current.Generation == 1,
        "fake authoritative positive reconciliation recognizes a committed transaction without retrying provider");
    Check(remote.Calls == 1, "fake reconciliation does not repeat provider verification");
}

{
    var current = NewStore();
    var handler = new SyntheticHttp(current.Subject);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Supabase:Url"] = "https://auth.example.test", ["Supabase:AnonKey"] = "fixture" }).Build();
    var auth = new SupabaseAuthService(new SyntheticFactory(handler), config, NullLogger<SupabaseAuthService>.Instance);
    var candidate = new PersistentSignupRetry(current, new SupabaseSignupRetryProvider(auth));
    Check(await candidate.VerifyAsync(id, secret, "123456") == SignupRetryResult.Completed && current.Generation == 1,
        "existing Supabase adapter verifies matching synthetic provider response");
    Check(handler.Calls == 2 && handler.UserCalls == 1, "synthetic provider receives one OTP verification and one independent user lookup");
}
foreach (var defect in new[] { "subject", "email", "unconfirmed", "rejected" })
{
    var current = NewStore();
    var handler = new SyntheticHttp(current.Subject) { UserDefect = defect };
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Supabase:Url"] = "https://auth.example.test", ["Supabase:AnonKey"] = "fixture" }).Build();
    var auth = new SupabaseAuthService(new SyntheticFactory(handler), config, NullLogger<SupabaseAuthService>.Instance);
    var candidate = new PersistentSignupRetry(current, new SupabaseSignupRetryProvider(auth));
    Check(await candidate.VerifyAsync(id, secret, "123456") == SignupRetryResult.Unconfirmed &&
        !current.Account && !current.Ready && handler.UserCalls == 1,
        "independent user lookup " + defect + " cannot establish a Customer binding");
}
{
    var current = NewStore();
    var handler = new SyntheticHttp(current.Subject) { Reject = true };
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Supabase:Url"] = "https://auth.example.test", ["Supabase:AnonKey"] = "fixture" }).Build();
    var auth = new SupabaseAuthService(new SyntheticFactory(handler), config, NullLogger<SupabaseAuthService>.Instance);
    var candidate = new PersistentSignupRetry(current, new SupabaseSignupRetryProvider(auth));
    Check(await candidate.VerifyAsync(id, secret, "123456") == SignupRetryResult.Unconfirmed && !current.Ready && !current.Account,
        "unverified provider rejection remains unconfirmed without reopening");
}
foreach (var scenario in new[]
{
    (Name: "expired OTP", Code: "otp_expired", Status: HttpStatusCode.BadRequest, Resend: false, Timeout: false),
    (Name: "rate-limited resend", Code: "over_email_send_rate_limit", Status: HttpStatusCode.TooManyRequests, Resend: true, Timeout: false),
    (Name: "provider failure", Code: "unexpected_failure", Status: HttpStatusCode.InternalServerError, Resend: false, Timeout: false),
    (Name: "provider timeout", Code: "", Status: HttpStatusCode.OK, Resend: false, Timeout: true)
})
{
    var current = NewStore();
    var handler = new SyntheticHttp(current.Subject) { Status = scenario.Status, ErrorCode = scenario.Code, Timeout = scenario.Timeout };
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Supabase:Url"] = "https://auth.example.test", ["Supabase:AnonKey"] = "fixture" }).Build();
    var auth = new SupabaseAuthService(new SyntheticFactory(handler), config, NullLogger<SupabaseAuthService>.Instance);
    var candidate = new PersistentSignupRetry(current, new SupabaseSignupRetryProvider(auth));
    var result = scenario.Resend ? await candidate.ResendAsync(id, secret) : await candidate.VerifyAsync(id, secret, "123456");
    Check(result == SignupRetryResult.Unconfirmed && !current.Ready && !current.Account && handler.Calls == 1,
        scenario.Name + " remains fenced and unconfirmed");
}

Console.WriteLine($"{checks} passed; in-memory fakes only, no PostgreSQL, MVC host or real provider.");

sealed class FakeStore(Guid id, string secret) : IPersistentSignupRetryStore
{
    private readonly object gate = new();
    private Guid? operation;
    public Guid Subject { get; } = Guid.NewGuid();
    public bool Ready { get; private set; } = true;
    public bool Expired { get; set; }
    public bool Cooldown { get; set; }
    public bool Account { get; private set; }
    public int Generation { get; private set; }
    public int BeginCalls { get; private set; }
    public string? CommitFault { get; set; }
    public bool ConfirmCommittedAfterFault { get; set; }
    public Task<SignupOperationClaim?> BeginAsync(Guid attemptId, string browserSecret, SignupOperation kind, CancellationToken ct)
    {
        lock (gate)
        {
            BeginCalls++;
            if (attemptId != id || browserSecret != secret || !Ready || Expired || (kind == SignupOperation.Resend && Cooldown))
                return Task.FromResult<SignupOperationClaim?>(null);
            Ready = false;
            operation = Guid.NewGuid();
            return Task.FromResult<SignupOperationClaim?>(new(operation.Value, "customer@example.test", Subject));
        }
    }
    public Task<bool> ReleaseAsync(Guid attemptId, string browserSecret, Guid operationId, CancellationToken ct)
    {
        lock (gate)
        {
            if (attemptId != id || browserSecret != secret || operation != operationId || Account || Expired) return Task.FromResult(false);
            operation = null;
            Ready = true;
            return Task.FromResult(true);
        }
    }
    public Task<bool> CommitAsync(Guid attemptId, string browserSecret, SignupOperationClaim claim, CancellationToken ct)
    {
        lock (gate)
        {
            if (attemptId != id || browserSecret != secret || operation != claim.OperationId || Account || Expired) return Task.FromResult(false);
            if (CommitFault == "before") throw new IOException();
            // Staged account changes are private until the fake transaction commits.
            var stagedAccount = true;
            if (CommitFault == "between") throw new IOException();
            Account = stagedAccount;
            Generation = 1;
            operation = null;
            if (CommitFault == "after" && !ConfirmCommittedAfterFault) throw new IOException();
            return Task.FromResult(true);
        }
    }
}

sealed class FakeProvider : IPersistentSignupRetryProvider
{
    public SignupProviderOutcome Outcome { get; set; } = SignupProviderOutcome.Confirmed;
    public Guid Subject { get; set; }
    public string? Defect { get; set; }
    public int Calls;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? Block { get; set; }
    public async Task<SignupProviderResult> VerifyAsync(string email, string code, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        Started.TrySetResult();
        if (Block != null) await Block.Task;
        if (Defect == "timeout") throw new TimeoutException();
        return new(Defect == "unknown" ? SignupProviderOutcome.Unconfirmed : Outcome,
            Defect == "subject" ? Guid.NewGuid() : Subject,
            Defect == "email" ? "other@example.test" : email,
            Defect != "unconfirmed-email", Defect != "missing-token");
    }
    public Task<SignupProviderResult> ResendAsync(string email, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new SignupProviderResult(Outcome));
    }
}

sealed class SyntheticFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

sealed class SyntheticHttp(Guid subject) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public int UserCalls { get; private set; }
    public string? UserDefect { get; set; }
    public bool Reject { get; set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string ErrorCode { get; set; } = "";
    public bool Timeout { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        if (request.RequestUri?.Host != "auth.example.test") throw new InvalidOperationException("Unexpected test origin.");
        if (request.RequestUri.AbsolutePath == "/auth/v1/user")
        {
            UserCalls++;
            if (request.Method != HttpMethod.Get || request.Headers.Authorization?.Parameter != "fixture")
                throw new InvalidOperationException("Expected an authenticated provider user lookup.");
            if (UserDefect == "rejected") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new { id = UserDefect == "subject" ? Guid.NewGuid() : subject,
                    email = UserDefect == "email" ? "other@example.test" : "customer@example.test",
                    email_confirmed_at = UserDefect == "unconfirmed" ? null : "2026-01-01T00:00:00Z" })) });
        }
        if (Timeout) throw new TaskCanceledException();
        if (Reject) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"msg\":\"invalid otp\"}") });
        if (Status != HttpStatusCode.OK)
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(JsonSerializer.Serialize(new { code = ErrorCode, msg = "synthetic failure" })) });
        var json = JsonSerializer.Serialize(new { access_token = "fixture", user = new { id = subject.ToString(), email = "customer@example.test", email_confirmed_at = "2026-01-01T00:00:00Z" } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
