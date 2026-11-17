using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using GlamByMariya.Infrastructure.Integrations;
using ImageMagick;

// Test-only host. This is not registered by the application and its login is
// synthetic; cookie protection does not authenticate a real provider.
internal static class CookieHostChecks
{
    internal static async Task RunAsync(PostgresTrustedIdentityBoundary boundary, NpgsqlDataSource backend, ProofCorrectionChecks.HttpFixture fixture, TrustedActorSession actor, Action<bool, string> check)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:DurableSessionEnforcement"] = "true" });
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddStudioCookieAuthentication(isDevelopment: true, durableEnforcement: true);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<TrustedRequestSessionAccessor>();
        builder.Services.AddSingleton<IStudioStore>(System.Reflection.DispatchProxy.Create<IStudioStore, PersistentStoreProxy>());
        builder.Services.AddSingleton<IVersionedAccountSecurityProvider>(new SyntheticSecurityProvider(actor));
        builder.Services.AddSingleton<VersionedSessionAuthorization>();
        var app = builder.Build();
        app.UseAuthentication();
        var objects = new FakeProofObjects();
        byte[] image;
        using (var source = new MagickImage(MagickColors.White, 8, 8))
        {
            source.Format = MagickFormat.Png;
            image = source.ToByteArray();
        }

        // Isolated fake-provider credential is generated per host and never logged.
        var fixtureCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        app.MapPost("/fixture-login/{kind}", async (HttpContext context, string kind) =>
        {
            if (context.Request.Headers["X-Fixture-Credential"] != fixtureCredential ||
                kind is not ("valid" or "expired" or "duplicate")) return Results.Unauthorized();
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, actor.AccountId.ToString("D")),
                new(ClaimTypes.Role, actor.Role),
                new(DurableSessionAuthorization.SubjectClaim, actor.ProviderSubject.ToString("D")),
                new(DurableSessionAuthorization.EpochClaim, actor.CredentialEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("account_version", new string('A', 64)),
                new(VersionedSessionAuthorization.SecurityVersionClaim, actor.SecurityGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
            if (kind == "duplicate") claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            var properties = kind == "expired"
                ? new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-2), ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1), AllowRefresh = false }
                : new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5), AllowRefresh = false };
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), properties);
            return Results.Ok();
        });
        app.MapPost("/fixture-update", async (HttpContext context, TrustedRequestSessionAccessor accessor) =>
        {
            if (await accessor.ResolveAsync() is null) return Results.Unauthorized();
            var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return result.Succeeded && await boundary.UpdateCustomerFullNameAsync(result.Principal, "Synthetic HTTP Customer")
                ? Results.Ok() : Results.Unauthorized();
        });
        app.MapPost("/fixture-proof", async (HttpContext context, TrustedRequestSessionAccessor accessor) =>
        {
            var session = await accessor.ResolveAsync();
            if (session == null) return Results.Unauthorized();
            if (!long.TryParse(context.Request.Query["revision"], out var revision)) return Results.BadRequest();
            var candidate = CustomerProofCorrectionCandidate.ForIsolatedTests(backend);
            return await candidate.CorrectAsync(session, context, fixture.ReservationId, revision, "HTTP-SYNTHETIC", fixture.AssetId)
                ? Results.Ok() : Results.BadRequest();
        });
        app.MapPost("/fixture-stage/{mode}", async (HttpContext context, TrustedRequestSessionAccessor accessor, string mode) =>
        {
            var session = await accessor.ResolveAsync();
            if (session == null) return Results.Unauthorized();
            objects.Mode = mode;
            var candidate = mode == "closed" ? new PaymentProofStagingCandidate(objects) : PaymentProofStagingCandidate.ForIsolatedTests(objects);
            var key = candidate.Stage(session, context, mode == "invalid" ? [1, 2, 3] : image);
            return key == null ? Results.BadRequest() : Results.Ok();
        });
        app.MapPost("/fixture-durable-stage", async (HttpContext context, TrustedRequestSessionAccessor accessor) =>
        {
            var session = await accessor.ResolveAsync();
            if (session == null) return Results.Unauthorized();
            var result = await PaymentProofStagingCandidate.ForIsolatedTests(objects).BeginDurableAsync(
                session, context, backend, fixture.ReservationId, 1, Guid.NewGuid(), image);
            return result == PaymentProofStagingCandidate.BeginResult.Accepted ? Results.Ok() : Results.BadRequest();
        });
        app.MapPost("/fixture-revoke", async (HttpContext context, TrustedRequestSessionAccessor accessor) =>
        {
            if (await accessor.ResolveAsync() is null) return Results.Unauthorized();
            var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return result.Succeeded && await boundary.RevokeMySessionsAsync(result.Principal) is not null
                ? Results.Ok() : Results.Unauthorized();
        });

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) };
            async Task<HttpStatusCode> Post(string path, string? cookie = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path);
                if (cookie != null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
                request.Headers.Add("X-Untrusted-Role", "SuperAdmin");
                using var response = await client.SendAsync(request);
                return response.StatusCode;
            }
            async Task<string> Login(string kind)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/fixture-login/" + kind);
                request.Headers.Add("X-Fixture-Credential", fixtureCredential);
                using var response = await client.SendAsync(request);
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("Synthetic fixture login failed.");
                return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            }

            check(await Post("/fixture-update") == HttpStatusCode.Unauthorized, "HTTP forged role header without cookie denied");
            check(await Post("/fixture-login/valid") == HttpStatusCode.Unauthorized, "fake provider denies missing fixture credential");
            var valid = await Login("valid");
            var middle = valid.Length / 2;
            var tampered = valid[..middle] + (valid[middle] == 'A' ? "B" : "A") + valid[(middle + 1)..];
            check(await Post("/fixture-update", tampered) == HttpStatusCode.Unauthorized, "HTTP tampered protected cookie denied");
            check(await Post("/fixture-update", await Login("expired")) == HttpStatusCode.Unauthorized, "HTTP expired protected cookie denied");
            check(await Post("/fixture-update", await Login("duplicate")) == HttpStatusCode.Unauthorized, "HTTP duplicate role ticket denied");
            check(await Post("/fixture-proof?revision=0") == HttpStatusCode.Unauthorized, "HTTP proof denies forged role without cookie");
            check(await Post("/fixture-proof", valid) == HttpStatusCode.BadRequest, "HTTP proof denies missing revision");
            check(await Post("/fixture-proof?revision=abc", valid) == HttpStatusCode.BadRequest, "HTTP proof denies malformed revision");
            check(await Post("/fixture-proof?revision=1", valid) == HttpStatusCode.BadRequest, "HTTP proof denies stale revision");
            check(await Post("/fixture-proof?revision=0", valid) == HttpStatusCode.OK, "HTTP proof correction uses trusted request session");
            check(await Post("/fixture-proof?revision=0", valid) == HttpStatusCode.BadRequest, "HTTP proof replay denied");
            check(await Post("/fixture-durable-stage", valid) == HttpStatusCode.OK, "HTTP durable staging intent uses trusted Customer and database revision");
            check(await Post("/fixture-stage/ok") == HttpStatusCode.Unauthorized, "HTTP staging denies missing session");
            check(await Post("/fixture-stage/invalid", valid) == HttpStatusCode.BadRequest && objects.Count == 0, "malformed staging image creates no object");
            check(await Post("/fixture-stage/closed", valid) == HttpStatusCode.BadRequest && objects.Count == 0, "non-test staging stays closed");
            check(await Post("/fixture-stage/corrupt", valid) == HttpStatusCode.BadRequest && objects.Count == 0, "readback mismatch denies staging and attempts cleanup");
            check(await Post("/fixture-stage/throw", valid) == HttpStatusCode.BadRequest && objects.Count == 0, "uncertain upload denies staging and attempts cleanup");
            check(await Post("/fixture-stage/ok", valid) == HttpStatusCode.OK && await Post("/fixture-stage/ok", valid) == HttpStatusCode.OK &&
                  objects.Count == 2 && objects.Keys.All(key => key.StartsWith("ProofStaging/") && key.EndsWith(".png")),
                "duplicate staging uses distinct server-generated private keys");
            check(await Post("/fixture-stage/delete-fails", valid) == HttpStatusCode.BadRequest && objects.Count == 3,
                "cleanup failure leaves an unaccepted orphan rather than reporting success");
            check(await Post("/fixture-update", valid) == HttpStatusCode.OK, "HTTP protected synthetic cookie reaches isolated Customer operation");
            check(await Post("/fixture-revoke", valid) == HttpStatusCode.OK, "HTTP synthetic session revocation succeeds");
            check(await Post("/fixture-update", valid) == HttpStatusCode.Unauthorized, "HTTP revoked cookie cannot repeat mutation");
            check(await Post("/fixture-stage/ok", valid) == HttpStatusCode.OK,
                "static fake cookie snapshot demonstrates staging alone does not detect database revocation");
            check(await Post("/fixture-durable-stage", valid) == HttpStatusCode.BadRequest,
                "database-revoked generation cannot create durable staging intent despite stale cookie snapshot");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class SyntheticSecurityProvider(TrustedActorSession actor) : IVersionedAccountSecurityProvider
    {
        public Task<VersionedAccountSecurity?> LoadAsync(Guid accountId, CancellationToken ct)
        {
            var snapshot = new AccountSecuritySnapshot(actor.AccountId, true, false, actor.Role,
                SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase, 1,
                actor.AccountId, actor.AccountId, SessionIdentityOwnerKind.Customer, true,
                AuthenticationIdentityPolicy.Supabase, actor.ProviderSubject,
                SessionCredentialState.Ready, actor.CredentialEpoch, new string('A', 64));
            return Task.FromResult<VersionedAccountSecurity?>(new(snapshot, actor.SecurityGeneration));
        }
    }
}

internal sealed class FakeProofObjects : IAppFileStore
{
    private readonly Dictionary<string, byte[]> objects = [];
    internal string Mode { get; set; } = "";
    internal int Count => objects.Count;
    internal IEnumerable<string> Keys => objects.Keys;
    public void Save(string key, byte[] bytes)
    {
        objects.Add(key, bytes.ToArray());
        if (Mode is "throw" or "delete-fails") throw new InvalidOperationException("Synthetic uncertain upload.");
    }
    public (byte[] Bytes, string ContentType)? Read(string? key) =>
        key != null && objects.TryGetValue(key, out var bytes)
            ? (Mode == "corrupt" ? [1] : bytes.ToArray(), "image/png") : null;
    public void Delete(string? key)
    {
        if (Mode == "delete-fails") throw new InvalidOperationException("Synthetic cleanup failure.");
        if (key != null) objects.Remove(key);
    }
}

public class PersistentStoreProxy : System.Reflection.DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
        method?.Name == "get_IsPersistent" ? true : throw new InvalidOperationException("Unexpected synthetic store operation.");
}
