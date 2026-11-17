using System.Text.RegularExpressions;
using System.Net.Http;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

static class ConcurrencyChecks
{
    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MakeupSystem.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "EmailCodeConcurrency";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(contentRoot, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    sealed class FakeHttpClientFactory : HttpMessageHandler, IHttpClientFactory
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> codes = new();
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        public string ReadCode(string email) => codes[email];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var email = json.RootElement.GetProperty("to")[0].GetProperty("email").GetString()!;
            var match = Regex.Match(json.RootElement.GetProperty("textContent").GetString()!, @"\b\d{6}\b");
            if (!match.Success) throw new InvalidOperationException("No verification code in synthetic delivery.");
            codes[email] = match.Value;
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
        }
    }

    static (EmailVerificationService Service, FakeHttpClientFactory Delivery) BuildService(string root)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Email:BrevoApiKey"] = "synthetic-key", ["Email:From"] = "sender@example.test" }).Build();
        var env = new FakeEnv(root) { ContentRootPath = root };
        var delivery = new FakeHttpClientFactory();
        var brevo = new BrevoEmailSender(delivery, config, NullLogger<BrevoEmailSender>.Instance);
        return (new EmailVerificationService(new MemoryCache(new MemoryCacheOptions()), config,
            NullLogger<EmailVerificationService>.Instance, env, brevo), delivery);
    }

    static async Task<int> Main()
    {
        string root = FindRepoRoot();
        int failures = 0;

        void Report(bool pass, string name, string? detail = null)
        {
            if (pass)
                Console.WriteLine($"PASS: {name}");
            else
            {
                failures++;
                Console.WriteLine($"FAIL: {name}" + (detail != null ? $" - {detail}" : ""));
            }
        }

        // Test 1: 10 parallel tasks, distinct emails, shared service instance.
        try
        {
            var (svc, delivery) = BuildService(root);
            var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
            {
                var email = $"concurrent{i}_{Guid.NewGuid():N}@test.local";
                var pending = new EmailVerificationService.PendingRegistration(
                    "Test", $"User{i}", email, 25, "Female", "123 Test St", "Password123!");
                await svc.RequestRegistrationCodeAsync(email, pending);
                var code = delivery.ReadCode(email);
                var result = svc.ConfirmRegistrationCode(email, code);
                if (result.Email != email)
                    throw new InvalidOperationException($"cross-talk: expected {email} got {result.Email}");
                if (result.FirstName != "Test" || result.LastName != $"User{i}")
                    throw new InvalidOperationException($"pending mismatch for {email}");
                return email;
            })).ToArray();

            try
            {
                await Task.WhenAll(tasks);
                var emails = tasks.Select(t => t.Result).ToList();
                bool distinct = emails.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 10;
                Report(distinct, "10 parallel distinct emails succeed independently",
                    distinct ? null : "duplicate emails returned");
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException agg
                    ? string.Join("; ", agg.InnerExceptions.Select(e => e.Message))
                    : ex.Message;
                Report(false, "10 parallel distinct emails succeed independently", inner);
            }
        }
        catch (Exception ex)
        {
            Report(false, "10 parallel distinct emails succeed independently", ex.Message);
        }

        // Test 2: wrong-code isolation.
        try
        {
            var (svc, delivery) = BuildService(root);
            var email = $"wrongcode_{Guid.NewGuid():N}@test.local";
            var pending = new EmailVerificationService.PendingRegistration(
                "Wrong", "Code", email, 30, "Female", "123 Test St", "Password123!");
            await svc.RequestRegistrationCodeAsync(email, pending);
            var correct = delivery.ReadCode(email);
            var wrong = correct == "000000" ? "111111" : "000000";
            bool threw = false;
            try
            {
                svc.ConfirmRegistrationCode(email, wrong);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            if (!threw)
            {
                Report(false, "wrong-code isolation", "wrong code did not throw");
            }
            else
            {
                try
                {
                    var result = svc.ConfirmRegistrationCode(email, correct);
                    Report(result.Email == email, "wrong-code isolation",
                        result.Email == email ? null : $"expected {email} got {result.Email}");
                }
                catch (Exception ex)
                {
                    Report(false, "wrong-code isolation", "correct code failed after wrong attempt: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Report(false, "wrong-code isolation", ex.Message);
        }

        // Test 3: resend cooldown.
        try
        {
            var (svc, _) = BuildService(root);
            var email = $"cooldown_{Guid.NewGuid():N}@test.local";
            var pending = new EmailVerificationService.PendingRegistration(
                "Cool", "Down", email, 28, "Female", "123 Test St", "Password123!");
            await svc.RequestRegistrationCodeAsync(email, pending);
            bool threw = false;
            string msg = "";
            try
            {
                await svc.RequestRegistrationCodeAsync(email, pending);
            }
            catch (InvalidOperationException ex)
            {
                threw = true;
                msg = ex.Message;
            }
            bool ok = threw && msg.Contains("Wait a minute", StringComparison.OrdinalIgnoreCase);
            Report(ok, "resend cooldown (second immediate request throws 'Wait a minute')",
                ok ? null : (threw ? $"wrong message: {msg}" : "second request did not throw"));
        }
        catch (Exception ex)
        {
            Report(false, "resend cooldown (second immediate request throws 'Wait a minute')", ex.Message);
        }

        // Test 4: expired/unknown email confirm must throw.
        try
        {
            var (svc, _) = BuildService(root);
            var email = $"unknown_{Guid.NewGuid():N}@test.local";
            bool threw = false;
            string msg = "";
            try
            {
                svc.ConfirmRegistrationCode(email, "123456");
            }
            catch (InvalidOperationException ex)
            {
                threw = true;
                msg = ex.Message;
            }
            bool ok = threw && (msg.Contains("Request a new one", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("expired", StringComparison.OrdinalIgnoreCase));
            Report(ok, "expired/unknown email confirm throws",
                ok ? null : (threw ? $"wrong message: {msg}" : "confirm did not throw"));
        }
        catch (Exception ex)
        {
            Report(false, "expired/unknown email confirm throws", ex.Message);
        }

        // One registration code must be consumed at most once under concurrent verification.
        try
        {
            var (svc, delivery) = BuildService(root);
            var email = $"single-use_{Guid.NewGuid():N}@example.test";
            await svc.RequestRegistrationCodeAsync(email, new("Test", "User", email, 25, "Others", "123 Test Street", "Password123!"));
            var code = delivery.ReadCode(email);
            var accepted = 0;
            Parallel.For(0, 100, _ => { try { svc.ConfirmRegistrationCode(email, code); Interlocked.Increment(ref accepted); } catch (InvalidOperationException) { } });
            Report(accepted == 1, "concurrent registration verification consumes a code at most once");
        }
        catch (Exception ex) { Report(false, "concurrent registration verification consumes a code at most once", ex.GetType().Name); }

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURER(S)");
        return failures == 0 ? 0 : 1;
    }
}
