using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

const string target = "glam_admin_mvc_candidate_test";
const string role = "glam_authz_test";
const string hash = "synthetic-mvc-hash-only";
var owner = Guid.Parse("55000000-0000-4000-8000-000000000001");
var admin = Guid.Parse("55000000-0000-4000-8000-000000000002");
var admin2 = Guid.Parse("55000000-0000-4000-8000-000000000003");
var subjects = new[] { Guid.Parse("66000000-0000-4000-8000-000000000001"),
    Guid.Parse("66000000-0000-4000-8000-000000000002"), Guid.Parse("66000000-0000-4000-8000-000000000003") };
NpgsqlConnectionStringBuilder Settings(string password) => new()
{
    Host = "127.0.0.1", Port = 5432, Database = target, Username = role,
    Password = password, Pooling = false, Timeout = 5
};
bool Allowed(string[] arguments, string? optIn) => arguments.SequenceEqual(["--exercise"]) && optIn == "YES";

if (args.SequenceEqual(["--offline"]))
{
    var s = Settings("synthetic");
    if (s.Host != "127.0.0.1" || s.Port != 5432 || s.Database != target || s.Username != role || s.Pooling ||
        !Allowed(["--exercise"], "YES") || Allowed(["--exercise", "other"], "YES") || Allowed(["--exercise"], null))
        throw new InvalidOperationException("Unsafe test settings.");
    var keys = new Queue<ConsoleKeyInfo>([new('a', ConsoleKey.A, false, false, false),
        new('\b', ConsoleKey.Backspace, false, false, false), new('b', ConsoleKey.B, false, false, false),
        new('\r', ConsoleKey.Enter, false, false, false)]);
    if (InteractivePassword.Read(keys.Dequeue) != "b") throw new InvalidOperationException("Password check failed.");
    try { InteractivePassword.Read(() => new('\r', ConsoleKey.Enter, false, false, false)); throw new Exception("Empty password accepted."); }
    catch (InvalidOperationException) { }
    var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema.test-only.sql"));
    foreach (var marker in new[] { "current_database() <> 'glam_admin_mvc_candidate_test'", "session_user <> 'glam_authz_test'",
        "pg_auth_members", "pg_default_acl", "inet_server_addr()", "ON_ERROR_STOP" })
        if (!schema.Contains(marker, StringComparison.Ordinal)) throw new InvalidOperationException("Schema guard missing.");
    var columns = IsolatedPreflight.ExpectedColumns.ToArray();
    if (!IsolatedPreflight.MatchesColumns(columns) || IsolatedPreflight.MatchesColumns(columns.Skip(1)) ||
        IsolatedPreflight.IsExclusive(true, 1) || IsolatedPreflight.IsFixtureEmpty(1))
        throw new InvalidOperationException("Preflight check failed.");
    var fake = new OfflineTransactionFactory();
    var fault = new LostAcknowledgementFactory(fake);
    fault.Arm();
    await using (var tx = await fault.BeginAsync(default))
    {
        try { await tx.CommitAsync(default); throw new Exception("Lost acknowledgement was not simulated."); }
        catch (IOException) { }
    }
    await using (var tx = await fault.BeginAsync(default)) await tx.CommitAsync(default);
    if (fake.Commits != 2) throw new InvalidOperationException("Fault wrapper check failed.");
    Console.WriteLine("5 grouped offline checks passed: fixed target and opt-in, hidden input, schema guard, isolated preflight, one-shot fault wrapper.");
    return;
}
if (!Allowed(args, Environment.GetEnvironmentVariable("GLAM_ADMIN_MVC_TEST_WRITES")))
    throw new InvalidOperationException("Separate approval, fixed target and explicit opt-in required.");

try
{
    if (Console.IsInputRedirected || Console.IsOutputRedirected) throw new InvalidOperationException("Interactive terminal required.");
    Console.Write("Restricted test-role password (hidden): ");
    var password = InteractivePassword.Read(() => Console.ReadKey(true));
    Console.WriteLine();
    var dataBuilder = new NpgsqlDataSourceBuilder(Settings(password).ConnectionString);
    async Task Guard(NpgsqlConnection c)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT current_database()='glam_admin_mvc_candidate_test'
              AND current_user='glam_authz_test' AND session_user='glam_authz_test'
              AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet
              AND inet_server_port()=5432
              AND EXISTS(SELECT 1 FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba
                         WHERE d.datname=current_database() AND r.rolname=current_user)
              AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolcanlogin
                         AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls)
              AND NOT EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member WHERE r.rolname=current_user)
              AND (SELECT count(*)=1 AND bool_and(name='synthetic-sprint3-only') FROM public.authz_sprint3_sentinel)
            """, c);
        if ((bool?)await cmd.ExecuteScalarAsync() != true) throw new InvalidOperationException("Unsafe test target.");
    }
    dataBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c));
    await using var source = dataBuilder.Build();
    await using var lease = await source.OpenConnectionAsync();
    await IsolatedPreflight.VerifyAsync(lease, requireEmpty: true);
    await using (var transaction = await lease.BeginTransactionAsync())
    {
        await using (var empty = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public.customers)+(SELECT count(*) FROM public.admin_users)+
                   (SELECT count(*) FROM public.account_identities)+(SELECT count(*) FROM public.account_security_generations)
            """, lease, transaction))
            if ((long?)await empty.ExecuteScalarAsync() != 0) throw new InvalidOperationException("Fixture is not empty.");
        var rows = new[] { (owner, "SuperAdmin", subjects[0], "owner@synthetic.invalid"),
            (admin, "Admin", subjects[1], "admin@synthetic.invalid"), (admin2, "Admin", subjects[2], "admin2@synthetic.invalid") };
        foreach (var (id, accountRole, subject, email) in rows)
        {
            await using var command = new NpgsqlCommand("""
                WITH u AS (INSERT INTO public.admin_users(app_id,username,email,role,is_active,password_hash)
                           VALUES(@id,@email,@email,@role,true,@hash) RETURNING id)
                INSERT INTO public.account_identities(id,admin_id,login,auth_state,supabase_user_id,credential_epoch,credential_sync_state)
                SELECT @id,u.id,@email,'Supabase',@subject,1,'Ready' FROM u;
                INSERT INTO public.account_security_generations(account_id,generation) VALUES(@id,1);
                """, lease, transaction);
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("email", email);
            command.Parameters.AddWithValue("role", accountRole); command.Parameters.AddWithValue("hash", hash);
            command.Parameters.AddWithValue("subject", subject);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:DurableSessionEnforcement"] = "true" });
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
    builder.Services.AddStudioCookieAuthentication(true, true);
    builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
    {
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });
    builder.Services.AddAuthorization();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
        .AddApplicationPart(typeof(AdminController).Assembly);
    builder.Services.AddSingleton<IControllerActivator, AdminOnlyActivator>();
    builder.Services.AddSingleton(source);
    builder.Services.AddDbContext<PublicMakeupDbContext>(o => o.UseNpgsql(source));
    // Only layout/profile display uses this synthetic store; the controller receives the actual public adapter.
    builder.Services.AddSingleton<IStudioStore>(new TestLayoutStore());
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton(sp => new AccountSettingsService(new DemoStore(), sp, sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
    builder.Services.AddVersionedAuthenticationCandidate();
    var fault = new LostAcknowledgementFactory(new PostgresAccountAuthorizationTransactionFactory(source));
    builder.Services.AddSingleton<IAccountAuthorizationTransactionFactory>(fault);
    builder.Services.AddScoped(sp => new AdminController(new PublicStudioStore(
        sp.GetRequiredService<PublicMakeupDbContext>(), sp.GetRequiredService<IHttpContextAccessor>(),
        sp.GetRequiredService<IWebHostEnvironment>(), sp.GetRequiredService<IConfiguration>(), null!),
        null!, null!, null!, null!, null!));
    var app = builder.Build();
    app.Use(async (context, next) =>
    {
        if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) ||
            context.Request.Headers["X-Synthetic-Test"].ToString() != nonce)
        { context.Response.StatusCode = 403; return; }
        await next();
    });
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllerRoute("mvc", "{controller=Home}/{action=Index}/{id?}");
    app.MapGet("/csrf", (HttpContext c, IAntiforgery a) => a.GetAndStoreTokens(c).RequestToken!);
    app.MapPost("/synthetic-session/{which}", async (string which, HttpContext c, IAntiforgery antiforgery) =>
    {
        try { await antiforgery.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        var selected = which switch { "owner" => (owner, "SuperAdmin", subjects[0]), "admin" => (admin, "Admin", subjects[1]), _ => (Guid.Empty, "", Guid.Empty) };
        if (selected.Item1 == Guid.Empty) return Results.NotFound();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, selected.Item1.ToString()), new Claim(ClaimTypes.Name, "Synthetic"),
            new Claim(ClaimTypes.Email, "synthetic@example.invalid"), new Claim(ClaimTypes.Role, selected.Item2),
            new Claim(DurableSessionAuthorization.EpochClaim, "1"), new Claim("account_version", AccountSession.Version(hash)),
            new Claim(DurableSessionAuthorization.SubjectClaim, selected.Item3.ToString()),
            new Claim(VersionedSessionAuthorization.SecurityVersionClaim, "1") };
        await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        return Results.Ok();
    });
    app.MapGet("/protected", () => Results.Ok()).RequireAuthorization();
    await app.StartAsync();
    try
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        HttpClient Client()
        {
            var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Add("X-Synthetic-Test", nonce);
            return client;
        }
        async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string>? form = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("X-CSRF-TOKEN", await client.GetStringAsync("/csrf"));
            if (form != null) request.Content = new FormUrlEncodedContent(form);
            return await client.SendAsync(request);
        }
        Dictionary<string, string> Form(Guid id, int generation) => new() { ["id"] = id.ToString(), ["expectedGeneration"] = generation.ToString() };
        async Task<(bool Active, long Generation)> State(Guid id)
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT u.is_active,g.generation FROM public.admin_users u
                JOIN public.account_security_generations g ON g.account_id=u.app_id WHERE u.app_id=@id
                """, connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Synthetic state unavailable.");
            return (reader.GetBoolean(0), reader.GetInt64(1));
        }
        var passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("Synthetic check failed."); passed++; Console.WriteLine("PASS " + label); }
        using var ownerClient = Client();
        using var adminClient = Client();
        Check((await Post(ownerClient, "/synthetic-session/owner")).IsSuccessStatusCode &&
              (await Post(adminClient, "/synthetic-session/admin")).IsSuccessStatusCode, "synthetic sessions issued");
        Check((await adminClient.GetAsync("/Admin/ManageAccounts")).StatusCode == HttpStatusCode.Forbidden, "Admin role denied");
        var page = await ownerClient.GetStringAsync("/Admin/ManageAccounts");
        Check(page.Contains("admin@synthetic.invalid") && page.Contains("admin2@synthetic.invalid") &&
              page.Contains("name=\"expectedGeneration\" value=\"1\"") && page.Contains("__RequestVerificationToken"),
            "SuperAdmin retrieves real PostgreSQL roster");
        Check((await Post(ownerClient, "/Admin/DisableAdminFromRoster", Form(admin, 2))).StatusCode == HttpStatusCode.Redirect,
            "stale request redirects");
        page = await ownerClient.GetStringAsync("/Admin/ManageAccounts");
        Check(page.Contains("change could not be confirmed", StringComparison.OrdinalIgnoreCase) && page.Contains("Generation: 1") &&
              await State(admin) == (true, 1L),
            "stale request refreshes authoritative state");
        Check((await Post(ownerClient, "/Admin/DisableAdminFromRoster", Form(admin, 1))).StatusCode == HttpStatusCode.Redirect,
            "Admin disable submitted");
        page = await ownerClient.GetStringAsync("/Admin/ManageAccounts");
        Check(page.Contains("disable request completed", StringComparison.OrdinalIgnoreCase) && page.Contains("Disabled") &&
              page.Contains("Generation: 2") && await State(admin) == (false, 2L),
            "disable and generation visible after refresh");
        Check((await adminClient.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, "disabled Admin cookie denied");
        fault.Arm();
        Check((await Post(ownerClient, "/Admin/DisableAdminFromRoster", Form(admin2, 1))).StatusCode == HttpStatusCode.Redirect,
            "simulated lost acknowledgement redirects");
        page = await ownerClient.GetStringAsync("/Admin/ManageAccounts");
        Check(page.Contains("change could not be confirmed", StringComparison.OrdinalIgnoreCase) && page.Contains("admin2@synthetic.invalid") &&
              !page.Contains("Confirm disable") && page.Contains("Generation: 2") && await State(admin2) == (false, 2L),
            "simulated lost acknowledgement refreshes committed status");
        Console.WriteLine($"{passed} checks passed; synthetic fixtures retained. No real Supabase or application startup used.");
    }
    finally { await app.StopAsync(); await app.DisposeAsync(); }
}
catch (Exception)
{
    Console.Error.WriteLine("The isolated test could not be confirmed; retain fixtures and review prerequisites without sharing credentials.");
    Environment.ExitCode = 1;
}

sealed class TestLayoutStore : DemoStore, IStudioStore { bool IStudioStore.IsPersistent => true; }
sealed class AdminOnlyActivator : IControllerActivator
{
    public object Create(ControllerContext context) => context.ActionDescriptor.ControllerTypeInfo.AsType() == typeof(AdminController)
        ? context.HttpContext.RequestServices.GetRequiredService<AdminController>() : throw new InvalidOperationException("Unexpected controller.");
    public void Release(ControllerContext context, object controller) { }
}
sealed class LostAcknowledgementFactory(IAccountAuthorizationTransactionFactory inner) : IAccountAuthorizationTransactionFactory
{
    private int armed;
    public void Arm() => Interlocked.Exchange(ref armed, 1);
    public async Task<IAccountAuthorizationTransaction> BeginAsync(CancellationToken ct) => new Transaction(await inner.BeginAsync(ct), this);
    private sealed class Transaction(IAccountAuthorizationTransaction inner, LostAcknowledgementFactory owner) : IAccountAuthorizationTransaction
    {
        public Task<(VersionedAccountSecurity? Actor, VersionedAccountSecurity? Target)> LockAccountsAsync(Guid actor, Guid target, CancellationToken ct) => inner.LockAccountsAsync(actor, target, ct);
        public Task<long?> AdvanceAsync(Guid id, long generation, CancellationToken ct) => inner.AdvanceAsync(id, generation, ct);
        public Task<bool> ApplyAsync(Guid target, AccountAuthorizationAction action, DateTimeOffset? until, CancellationToken ct) => inner.ApplyAsync(target, action, until, ct);
        public async Task CommitAsync(CancellationToken ct)
        {
            await inner.CommitAsync(ct);
            if (Interlocked.Exchange(ref owner.armed, 0) == 1) throw new IOException("Synthetic lost acknowledgement.");
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

sealed class OfflineTransactionFactory : IAccountAuthorizationTransactionFactory
{
    public int Commits { get; private set; }
    public Task<IAccountAuthorizationTransaction> BeginAsync(CancellationToken ct) => Task.FromResult<IAccountAuthorizationTransaction>(new Transaction(this));
    private sealed class Transaction(OfflineTransactionFactory owner) : IAccountAuthorizationTransaction
    {
        public Task<(VersionedAccountSecurity? Actor, VersionedAccountSecurity? Target)> LockAccountsAsync(Guid actor, Guid target, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> AdvanceAsync(Guid id, long generation, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ApplyAsync(Guid target, AccountAuthorizationAction action, DateTimeOffset? until, CancellationToken ct) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken ct) { owner.Commits++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
