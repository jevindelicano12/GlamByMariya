using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

// Only accepts an ephemeral local test port. Never reads User Secrets or env
// credentials. The runner initializes a brand-new native PostgreSQL cluster.
if (args.Length != 1 || !int.TryParse(args[0], out var port) || port < 1024 || port > 65535)
    throw new InvalidOperationException("Pass the isolated PostgreSQL test port, never a production connection string.");
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

IConfiguration Configuration(string? poolSize = null, string? supplied = null)
{
    var values = new Dictionary<string, string?>
    {
        ["ConnectionStrings:MakeupDatabase"] = supplied ?? "Host=unit-test.invalid;Database=test;Username=test;Password=fixture;Maximum Pool Size=100;Minimum Pool Size=10;Pooling=false",
    };
    if (poolSize != null) values["Database:MaxPoolSize"] = poolSize;
    return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
var parsed = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(Configuration()));
Check(parsed.Pooling && parsed.MaxPoolSize == 4 && parsed.MinPoolSize == 0,
    "default shared budget overrides unsafe supplied max/min/pooling settings");
Check(parsed.ConnectionIdleLifetime == 60 && parsed.ConnectionPruningInterval == 10,
    "idle connections are pruned without disabling pooling");
Check(parsed.SslMode == SslMode.VerifyFull && !parsed.IncludeErrorDetail && !parsed.PersistSecurityInfo,
    "pool changes preserve TLS VerifyFull and secret protections");
Check(parsed.Timeout == 15 && parsed.CommandTimeout == 30, "existing bounded timeouts are preserved");
Check(new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(Configuration("2"))).MaxPoolSize == 2,
    "server-side Database:MaxPoolSize is configurable");
foreach (var bad in new[] { "0", "7", "-1", "oops" })
{
    try { DatabaseConfiguration.ConnectionString(Configuration(bad)); throw new Exception("Invalid pool budget accepted"); }
    catch (InvalidOperationException) { Check(true, "invalid pool budget is refused"); }
}
var uri = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(Configuration(supplied:
    "postgresql://test:fixture@unit-test.invalid:5432/test")));
Check(uri.MaxPoolSize == 4 && uri.Pooling && uri.SslMode == SslMode.VerifyFull,
    "URI and Npgsql key/value inputs receive the same bounded policy");

// No TLS on this throwaway loopback cluster only. Production config above
// still requires VerifyFull. No production settings are modified.
var local = new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = port, Database = "postgres", Username = "postgres",
    SslMode = SslMode.Disable, MaxPoolSize = 4, MinPoolSize = 0, Timeout = 5,
    ApplicationName = "IsolatedConnectionChecks"
};
await using var dataSource = NpgsqlDataSource.Create(local.ConnectionString);
DbContext NewContext() => new(new DbContextOptionsBuilder().UseNpgsql(dataSource).Options);

// Disposal success, query failure and nested caller-owned lease. The context
// remains alive: successful cleanup must not depend on disposing the request.
await using (var context = NewContext())
{
    await using (var lease = await DatabaseConnectionLease.OpenAsync(context.Database))
        Check(context.Database.GetDbConnection().State == ConnectionState.Open, "explicit lease opens EF connection");
    Check(context.Database.GetDbConnection().State == ConnectionState.Closed, "lease returns connection before context disposal");
    try
    {
        await using var lease = await DatabaseConnectionLease.OpenAsync(context.Database);
        await context.Database.ExecuteSqlRawAsync("SELECT 1/0");
    }
    catch (PostgresException) { }
    Check(context.Database.GetDbConnection().State == ConnectionState.Closed, "query failure releases the explicitly opened connection");
    await context.Database.OpenConnectionAsync();
    await using (var nested = await DatabaseConnectionLease.OpenAsync(context.Database)) { }
    Check(context.Database.GetDbConnection().State == ConnectionState.Open, "nested lease does not close caller-owned open reference");
    await context.Database.CloseConnectionAsync();
    Check(context.Database.GetDbConnection().State == ConnectionState.Closed, "caller releases its own open reference");

}
await using (var db = new MakeupDbContext(new DbContextOptionsBuilder<MakeupDbContext>().UseNpgsql(dataSource).Options))
{
    await DatabaseSetup.ExecuteScriptAsync(db, "SELECT 1;");
    Check(db.Database.GetDbConnection().State == ConnectionState.Closed, "successful ExecuteScript releases its connection");
    try { await DatabaseSetup.ExecuteScriptAsync(db, "SELECT 1/0;"); }
    catch (PostgresException) { }
    Check(db.Database.GetDbConnection().State == ConnectionState.Closed, "ExecuteScript failure closes connection after rollback");
    try { await DatabaseInspection.VerifyApplicationAsync(db, Configuration()); }
    catch (Exception e) when (e is InvalidOperationException or PostgresException) { }
    Check(db.Database.GetDbConnection().State == ConnectionState.Closed, "inspection failure closes its explicit lease");
    try { await PublicDatabaseProfile.RunAsync(db, Path.GetTempPath()); }
    catch (PostgresException) { }
    Check(db.Database.GetDbConnection().State == ConnectionState.Closed, "profiling failure rolls back and releases its lease");
    using (var transaction = await db.Database.BeginTransactionAsync())
    {
        try { await db.Database.ExecuteSqlRawAsync("SELECT 1/0"); }
        catch (PostgresException) { }
    }
    Check(db.Database.GetDbConnection().State == ConnectionState.Closed, "disposed failing transaction releases its connection");
}
await using (var failedContext = new DbContext(new DbContextOptionsBuilder().UseNpgsql(
    "Host=127.0.0.1;Port=1;Username=isolated;Database=isolated;Timeout=1;SSL Mode=Disable;Maximum Pool Size=1").Options))
{
    try { await DatabaseConnectionLease.OpenAsync(failedContext.Database); }
    catch (Exception e) when (e is NpgsqlException or InvalidOperationException) { }
    Check(failedContext.Database.GetDbConnection().State == ConnectionState.Closed,
        "failed connection open does not leave a borrowed session");
}

// 24 concurrent operations must queue behind the same 4 physical connections.
var pids = new ConcurrentDictionary<int, byte>();
await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT pg_backend_pid() FROM pg_sleep(0.04)";
    pids.TryAdd((int)(await cmd.ExecuteScalarAsync())!, 0);
}));
Check(pids.Count <= 4, "24 concurrent operations use no more than 4 PostgreSQL sessions");
var contextPids = new ConcurrentDictionary<int, byte>();
await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
{
    await using DbContext context = index % 2 == 0
        ? new PublicMakeupDbContext(new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(dataSource).Options)
        : new DataProtectionKeyContext(new DbContextOptionsBuilder<DataProtectionKeyContext>().UseNpgsql(dataSource).Options);
    var pid = await context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\" FROM pg_sleep(0.04)").SingleAsync();
    contextPids.TryAdd(pid, 0);
}));
Check(contextPids.Count <= 4, "24 concurrent app/key-ring context queries share the four-session ceiling");

// Hold every slot, show the next borrower waits, release a slot, then confirm
// it completes. No retries or extra data sources are used to evade the limit.
var held = new List<NpgsqlConnection>();
try
{
    for (int i=0; i<4; i++) held.Add(await dataSource.OpenConnectionAsync());
    var waiting = dataSource.OpenConnectionAsync().AsTask();
    await Task.Delay(100);
    Check(!waiting.IsCompleted, "fifth borrower waits instead of opening a fifth server connection");
    await held[0].DisposeAsync();
    held.RemoveAt(0);
    await using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    Check(resumed.State == ConnectionState.Open, "queued borrower resumes when a pooled session is returned");
}
finally { foreach (var c in held) await c.DisposeAsync(); }

// Data Protection: encrypted keys persist on the isolated DB, use the SAME
// source as repositories, and can decrypt after provider/context recreation.
using var rsa = RSA.Create(2048);
var request = new CertificateRequest("CN=Ephemeral Test Only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30));
ServiceProvider NewProvider()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<DataProtectionKeyContext>(o => o.UseNpgsql(dataSource));
    services.AddDbContext<PublicMakeupDbContext>(o => o.UseNpgsql(dataSource));
    services.AddDataProtection().SetApplicationName("GlamByMariyaMakeupSystem")
        .PersistKeysToDbContext<DataProtectionKeyContext>().ProtectKeysWithCertificate(certificate);
    return services.BuildServiceProvider();
}
await using (var context = new DataProtectionKeyContext(new DbContextOptionsBuilder<DataProtectionKeyContext>().UseNpgsql(dataSource).Options))
    await context.Database.EnsureCreatedAsync(); // Isolated test database only.
string protectedValue;
using (var provider = NewProvider())
{
    using var scope = provider.CreateScope();
    var keys = scope.ServiceProvider.GetRequiredService<DataProtectionKeyContext>();
    var app = scope.ServiceProvider.GetRequiredService<PublicMakeupDbContext>();
    Check(keys.Database.GetConnectionString() == app.Database.GetConnectionString(),
        "key-ring and app contexts target the same pool configuration");
    protectedValue = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("connection-test").Protect("session-test");
    var xml = await keys.DataProtectionKeys.Select(k => k.Xml).FirstAsync();
    Check(xml != null && XDocument.Parse(xml).Descendants().Any(e => e.Name.LocalName == "encryptedSecret"),
        "Data Protection stores certificate-encrypted key XML");
}
using (var restarted = NewProvider())
    Check(restarted.GetRequiredService<IDataProtectionProvider>().CreateProtector("connection-test").Unprotect(protectedValue) == "session-test",
        "Data Protection can decrypt after provider restart with persisted keys");

var rejection = new PostgresException("(EMAXCONNSESSION) private-provider-marker", "FATAL", "FATAL", "XX000");
var http = new DefaultHttpContext();
http.Response.Body = new MemoryStream();
var middleware = new DatabaseCapacityMiddleware(_ => throw new InvalidOperationException("wrapped", rejection), NullLogger<DatabaseCapacityMiddleware>.Instance);
await middleware.InvokeAsync(http);
Check(http.Response.StatusCode == 503 && http.Response.Headers.RetryAfter == "30", "session capacity rejection returns honest 503, not 500 or success");
Check(!DatabaseDiagnostics.Describe(rejection).Contains("private-provider-marker"), "capacity diagnostics do not expose raw provider messages");
Check(!DatabaseDiagnostics.IsSessionPoolExhausted(new PostgresException("unrelated error", "ERROR", "ERROR", "XX000")),
    "unrelated XX000 database errors are not hidden as pool-capacity failures");
Console.WriteLine($"All {checks} pool, disposal and Data Protection checks passed (isolated PostgreSQL only).");
