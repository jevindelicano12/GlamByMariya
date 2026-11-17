using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlamByMariya.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

var failures = 0;
void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    if (!condition) failures++;
}

var blockedRequests = 0;
var disabledConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Email:DeliveryDisabled"] = "true",
    ["Email:BrevoApiKey"] = "synthetic-disabled-key",
    ["Email:From"] = "verified@example.test",
    ["Email:SmtpHost"] = "smtp.example.test"
}).Build();
var blockedFactory = new StubFactory(new DelegateHandler((_, _) =>
{
    blockedRequests++;
    throw new InvalidOperationException("Disabled delivery attempted HTTP.");
}));
var disabledSender = new BrevoEmailSender(blockedFactory, disabledConfig, NullLogger<BrevoEmailSender>.Instance);
try { await disabledSender.SendAsync("recipient@example.test", "Test", "Test"); Check(false, "disabled direct send"); }
catch (InvalidOperationException e) { Check(e.Message == "Email delivery is disabled for this environment.", "disabled direct sender refuses delivery"); }
using (var disabledCache = new MemoryCache(new MemoryCacheOptions()))
{
    var disabledCodes = new EmailVerificationService(disabledCache, disabledConfig,
        NullLogger<EmailVerificationService>.Instance, new TestEnvironment(), disabledSender);
    try { await disabledCodes.SendEmailAsync("recipient@example.test", "Test", "Test"); Check(false, "disabled email workflow"); }
    catch (InvalidOperationException e) { Check(e.Message == "Email delivery is disabled for this environment.", "disabled workflow refuses API, SMTP and development outbox delivery"); }
}
Check(blockedRequests == 0, "disabled delivery makes no HTTP requests");
foreach (var missing in new[] { "Email:BrevoApiKey", "Email:From" })
{
    var incomplete = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Email:BrevoApiKey"] = missing == "Email:BrevoApiKey" ? "" : "synthetic-test-key",
        ["Email:From"] = missing == "Email:From" ? "" : "sender@example.test"
    }).Build();
    try
    {
        await new BrevoEmailSender(blockedFactory, incomplete, NullLogger<BrevoEmailSender>.Instance)
            .SendAsync("recipient@example.test", "Test", "Test");
        Check(false, "missing sender configuration: " + missing);
    }
    catch (InvalidOperationException e)
    {
        Check(blockedRequests == 0 && e.Message.Contains("not configured", StringComparison.Ordinal),
            "missing configuration prevents HTTP: " + missing);
    }
}

BrevoEmailSender Build(HttpMessageHandler handler, string apiKey = "test-key-secret")
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Email:BrevoApiKey"] = apiKey,
        ["Email:From"] = "verified@example.test",
    }).Build();
    var factory = new StubFactory(handler);
    return new BrevoEmailSender(factory, config, NullLogger<BrevoEmailSender>.Instance);
}

// Accepted request: verify HTTPS endpoint, API-key header, verified sender and body.
var successHandler = new DelegateHandler((request, _) =>
{
    Check(request.Method == HttpMethod.Post, "Brevo uses POST");
    Check(request.RequestUri?.AbsoluteUri == "https://api.brevo.com/v3/smtp/email", "uses official Brevo HTTPS endpoint");
    Check(request.Headers.TryGetValues("api-key", out var values) && values.Single() == "test-key-secret", "API key sent only in server-side header");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
    {
        Content = new StringContent("{\"messageId\":\"accepted\"}")
    });
});
try
{
    await Build(successHandler).SendAsync("customer@example.test", "Verify email", "Your code is 123456.");
    var json = JsonDocument.Parse(successHandler.Body!);
    Check(json.RootElement.GetProperty("sender").GetProperty("email").GetString() == "verified@example.test", "preserves configured verified sender");
    Check(json.RootElement.GetProperty("sender").GetProperty("name").GetString() == "Glam by Mariya", "uses Glam by Mariya sender display name");
    Check(json.RootElement.GetProperty("textContent").GetString() == "Your code is 123456.", "preserves existing plain-text email template");
    Check(json.RootElement.GetProperty("to")[0].GetProperty("email").GetString() == "customer@example.test", "sends to requested recipient");
}
catch (Exception e) { Check(false, "successful API send: " + e.GetType().Name); }

// Verify the branding passed through the real email workflow templates.
var templateHandler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
var templateConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Email:BrevoApiKey"] = "test-key-secret",
    ["Email:From"] = "verified@example.test",
}).Build();
using var templateCache = new MemoryCache(new MemoryCacheOptions());
var templateCodes = new EmailVerificationService(templateCache, templateConfig,
    NullLogger<EmailVerificationService>.Instance, new TestEnvironment(),
    new BrevoEmailSender(new StubFactory(templateHandler), templateConfig, NullLogger<BrevoEmailSender>.Instance));
await templateCodes.RequestRegistrationCodeAsync("registration@example.test",
    new EmailVerificationService.PendingRegistration("Test", "Person", "registration@example.test", 25, "", "123 Test Street", "Password123!"));
using (var verification = JsonDocument.Parse(templateHandler.Body!))
{
    var subject = verification.RootElement.GetProperty("subject").GetString();
    var body = verification.RootElement.GetProperty("textContent").GetString()!;
    Check(subject == "Your Glam by Mariya Verification Code", "registration uses required verification subject");
    Check(Regex.IsMatch(body, @"^Your Glam by Mariya verification code is \d{6}\. It expires in 15 minutes\. If you did not request this, please ignore this email\.$"),
        "registration uses required verification body and expiry text");
    var code = Regex.Match(body, @"\b\d{6}\b").Value;
    var incorrect = code == "000000" ? "111111" : "000000";
    try { templateCodes.ConfirmRegistrationCode("registration@example.test", incorrect); Check(false, "incorrect registration code is rejected"); }
    catch (InvalidOperationException) { Check(true, "incorrect registration code is rejected"); }
    Check(templateCodes.ConfirmRegistrationCode("registration@example.test", code).Email == "registration@example.test", "correct registration code consumes the pending account");
    try { templateCodes.ConfirmRegistrationCode("registration@example.test", code); Check(false, "reused registration code is rejected"); }
    catch (InvalidOperationException) { Check(true, "reused registration code is rejected"); }
}
await templateCodes.RequestRegistrationCodeAsync("expired@example.test",
    new EmailVerificationService.PendingRegistration("Test", "Person", "expired@example.test", 25, "", "123 Test Street", "Password123!"));
using (var verification = JsonDocument.Parse(templateHandler.Body!))
{
    var code = Regex.Match(verification.RootElement.GetProperty("textContent").GetString()!, @"\b\d{6}\b").Value;
    const string key = "emailcode:code:expired@example.test";
    var entry = templateCache.Get(key)!;
    var hash = entry.GetType().GetProperty("CodeHash")!.GetValue(entry)!;
    var expired = Activator.CreateInstance(entry.GetType(), hash, DateTime.UtcNow.AddSeconds(-1))!;
    templateCache.Set(key, expired, TimeSpan.FromMinutes(1));
    try { templateCodes.ConfirmRegistrationCode("expired@example.test", code); Check(false, "expired registration code is rejected"); }
    catch (InvalidOperationException e) { Check(e.Message.Contains("expired", StringComparison.OrdinalIgnoreCase), "expired registration code is rejected"); }
}
await templateCodes.RequestPasswordResetCodeAsync("reset@example.test");
using (var reset = JsonDocument.Parse(templateHandler.Body!))
{
    Check(reset.RootElement.GetProperty("subject").GetString() == "Your Glam by Mariya Password Reset Code"
        && reset.RootElement.GetProperty("textContent").GetString()!.Contains("Your Glam by Mariya password reset code is ", StringComparison.Ordinal),
        "password reset email uses Glam by Mariya branding");
}
await templateCodes.SendEmailAsync("invite@example.test", "You are invited to administer Glam by Mariya",
    "A Super Admin invited you to administer Glam by Mariya. Existing invitation link and expiry are unchanged.");
using (var invite = JsonDocument.Parse(templateHandler.Body!))
{
    Check(invite.RootElement.GetProperty("subject").GetString() == "You are invited to administer Glam by Mariya"
        && invite.RootElement.GetProperty("textContent").GetString()!.Contains("Glam by Mariya", StringComparison.Ordinal),
        "administrator invitation email uses Glam by Mariya branding");
}

// Rejected by Brevo: safe exception, no raw response/API key disclosure.
try
{
    await Build(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
    {
        Content = new StringContent("test-key-secret rejected")
    }))).SendAsync("customer@example.test", "Verify email", "code");
    Check(false, "Brevo API failure is rejected");
}
catch (InvalidOperationException e)
{
    Check(!e.Message.Contains("test-key-secret", StringComparison.Ordinal), "Brevo API failure is rejected without exposing secrets");
}

// A rejected delivery must not leave a pending registration that appears sent.
var registrationConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Email:BrevoApiKey"] = "test-key-secret",
    ["Email:From"] = "verified@example.test",
}).Build();
var registrationEnv = new TestEnvironment();
var registrationCodes = new EmailVerificationService(new MemoryCache(new MemoryCacheOptions()), registrationConfig,
    NullLogger<EmailVerificationService>.Instance, registrationEnv,
    new BrevoEmailSender(new StubFactory(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))),
        registrationConfig, NullLogger<BrevoEmailSender>.Instance));
try
{
    await registrationCodes.RequestRegistrationCodeAsync("person@example.test",
        new EmailVerificationService.PendingRegistration("Test", "Person", "person@example.test", 25, "", "123 Test Street", "Password123!"));
    Check(false, "rejected Brevo send aborts registration verification");
}
catch (InvalidOperationException)
{
    Check(!registrationCodes.HasPendingRegistration("person@example.test"), "rejected Brevo send does not mark code/account as pending-sent");
}

// Timeout: caller gets a bounded, safe retry message rather than hanging.
try
{
    await Build(new DelegateHandler((_, _) => throw new TaskCanceledException("simulated timeout")))
        .SendAsync("customer@example.test", "Verify email", "code");
    Check(false, "Brevo timeout is handled");
}
catch (InvalidOperationException e)
{
    Check(e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase), "timeout maps to a safe retryable error");
}

Console.WriteLine(failures == 0 ? "ALL BREVO SENDER CHECKS PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public string? Body { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await send(request, cancellationToken);
    }
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "EmailSenderChecks";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "email-sender-checks");
    public string EnvironmentName { get; set; } = Environments.Production;
    public string WebRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "email-sender-checks-wwwroot");
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
