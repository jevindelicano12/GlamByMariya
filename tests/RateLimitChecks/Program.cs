using System.Text.RegularExpressions;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

// ---- 1. Policy names and thresholds are a single source of truth. ----
var expected = new Dictionary<string, (int Limit, TimeSpan Window)>
{
    [RateLimitPolicies.Login] = (60, TimeSpan.FromMinutes(1)),
    [RateLimitPolicies.Register] = (5, TimeSpan.FromMinutes(10)),
    [RateLimitPolicies.VerifyCode] = (15, TimeSpan.FromMinutes(5)),
    [RateLimitPolicies.ResendCode] = (5, TimeSpan.FromMinutes(5)),
    [RateLimitPolicies.ForgotPassword] = (5, TimeSpan.FromMinutes(10)),
    [RateLimitPolicies.ResetPassword] = (10, TimeSpan.FromMinutes(10)),
    [RateLimitPolicies.AcceptInvite] = (10, TimeSpan.FromMinutes(10)),
    [RateLimitPolicies.InviteAdmin] = (10, TimeSpan.FromMinutes(10)),
};
Check(RateLimitPolicies.All.Count == expected.Count, "expected number of rate-limit policies is defined");
foreach (var (name, rule) in expected)
{
    Check(RateLimitPolicies.All.TryGetValue(name, out var actual) && actual is not null, $"policy '{name}' is registered");
    Check(actual!.PermitLimit == rule.Limit && actual.Window == rule.Window,
        $"policy '{name}' threshold is {rule.Limit} per {rule.Window.TotalMinutes:0.#} minutes");
}

// ---- 2. LoginAttemptLimiter: account-aware behaviour is unchanged. ----
var limiter = new LoginAttemptLimiter(new ManualTimeProvider(baseTime));
var results = Enumerable.Range(0, 11).Select(_ => limiter.TryAcquire("customer@example.test", "198.51.100.10")).ToArray();
Check(results.Take(10).All(x => x) && !results[10], "login limiter permits 10 attempts per IP+email then refuses the 11th");
Check(limiter.TryAcquire("other@example.test", "198.51.100.10"), "login limiter keeps independent windows per account");
Check(limiter.TryAcquire("customer@example.test", "198.51.100.11"), "login limiter keeps independent windows per client address");

// ---- 3. Simultaneous attempts still land on exactly the permit limit. ----
var concurrent = new LoginAttemptLimiter(TimeProvider.System);
var permitted = 0;
Parallel.For(0, 48, _ =>
{
    if (concurrent.TryAcquire("race@example.test", "203.0.113.7")) Interlocked.Increment(ref permitted);
});
Check(permitted == LoginAttemptLimiter.Limit,
    $"concurrent attempts permit exactly {LoginAttemptLimiter.Limit} (observed {permitted})");

// ---- 4. Expired windows are pruned and become usable again. ----
var clock = new ManualTimeProvider(baseTime);
var expiring = new LoginAttemptLimiter(clock);
for (var i = 0; i < LoginAttemptLimiter.Limit; i++) expiring.TryAcquire("old@example.test", "192.0.2.1");
Check(!expiring.TryAcquire("old@example.test", "192.0.2.1"), "expired-window setup: the 11th attempt is refused");
Check(expiring.TrackedKeyCount == 1, "one login window is tracked");
clock.Advance(TimeSpan.FromSeconds(61));
Check(expiring.TryAcquire("new@example.test", "192.0.2.1"), "a new window is accepted after the cleanup interval");
Check(expiring.TrackedKeyCount == 1, "the expired login window is pruned during the scheduled sweep");
Check(Enumerable.Range(0, LoginAttemptLimiter.Limit).All(_ => expiring.TryAcquire("old@example.test", "192.0.2.1")),
    "a pruned account window starts a fresh allowance");

// ---- 5. Hard key ceiling bounds memory under sustained distinct keys. ----
var growthClock = new ManualTimeProvider(baseTime);
var bounded = new LoginAttemptLimiter(growthClock);
growthClock.Advance(TimeSpan.FromSeconds(30));
for (var i = 0; i <= LoginAttemptLimiter.MaxTrackedKeys + 49; i++)
    bounded.TryAcquire($"bulk{i}@example.test", "203.0.113.9");
Check(bounded.TrackedKeyCount <= LoginAttemptLimiter.MaxTrackedKeys,
    $"key count stays bounded during bulk acquisition ({bounded.TrackedKeyCount})");
growthClock.Advance(TimeSpan.FromSeconds(31));
Check(bounded.TryAcquire("trigger@example.test", "203.0.113.9"), "sweep trigger request is accepted");
Check(bounded.TrackedKeyCount <= LoginAttemptLimiter.MaxTrackedKeys,
    $"key count stays bounded after the scheduled sweep ({bounded.TrackedKeyCount})");

// ---- 6. OTP resend cooldown and attempt limit remain in force. ----
var root = Path.Combine(Path.GetTempPath(), "makeup-rate-limit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Email:BrevoApiKey"] = "synthetic-key", ["Email:From"] = "sender@example.test" }).Build();
    var env = new TestEnv(root);
    var delivery = new CaptureHttpClientFactory();
    var brevo = new BrevoEmailSender(delivery, config, NullLogger<BrevoEmailSender>.Instance);
    var mail = new EmailVerificationService(cache, config, NullLogger<EmailVerificationService>.Instance, env, brevo);

    var cooldownEmail = "cooldown@example.test";
    var cooldownPending = Pending(cooldownEmail);
    await mail.RequestRegistrationCodeAsync(cooldownEmail, cooldownPending);
    var cooldownHeld = false;
    try { await mail.RequestRegistrationCodeAsync(cooldownEmail, cooldownPending); }
    catch (InvalidOperationException e) { cooldownHeld = e.Message.Contains("Wait a minute", StringComparison.OrdinalIgnoreCase); }
    Check(cooldownHeld, "registration resend cooldown is preserved (60 seconds)");

    var attemptsEmail = "attempts@example.test";
    await mail.RequestRegistrationCodeAsync(attemptsEmail, Pending(attemptsEmail));
    var correct = delivery.Code;
    var wrong = correct == "000000" ? "111111" : "000000";
    for (var i = 0; i < 5; i++)
    {
        try { mail.ConfirmRegistrationCode(attemptsEmail, wrong); } catch (InvalidOperationException) { }
    }
    var locked = false;
    try { mail.ConfirmRegistrationCode(attemptsEmail, correct); }
    catch (InvalidOperationException e) { locked = e.Message.Contains("Too many wrong codes", StringComparison.OrdinalIgnoreCase); }
    Check(locked, "five wrong OTP attempts still lock the code, even for the correct value");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine($"All {checks} rate-limit and limiter memory checks passed.");

static EmailVerificationService.PendingRegistration Pending(string email) =>
    new("Rate", "Limit", email, 25, "Female", "123 Rate Limit Street", "RateLimit123!");

sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}

sealed class CaptureHttpClientFactory : HttpMessageHandler, IHttpClientFactory
{
    public string Code { get; private set; } = "";
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var json = System.Text.Json.JsonDocument.Parse(text);
        Code = Regex.Match(json.RootElement.GetProperty("textContent").GetString()!, @"\b\d{6}\b").Value;
        return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
    }
}

sealed class TestEnv(string contentRoot) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "RateLimitChecks";
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = Path.Combine(contentRoot, "wwwroot");
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
