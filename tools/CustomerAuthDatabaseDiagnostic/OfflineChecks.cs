using System.Reflection;
using System.Diagnostics.Tracing;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using GlamByMariya.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal static class OfflineChecks
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Offline diagnostic check failed.");
            checks++;
        }
        const string poison = "PRIVATE-host-password-email-token-certificate-query";
        var values = new Dictionary<string, string?>
        {
            ["Deployment:Profile"] = "v2-customer-auth-test", ["DOTNET_ENVIRONMENT"] = "V2CustomerAuthTest",
            ["ASPNETCORE_ENVIRONMENT"] = "V2CustomerAuthTest", ["Deployment:V2ProjectId"] = Program.Project,
            ["Database:ProjectId"] = Program.Project, ["Supabase:Url"] = $"https://{Program.Project}.supabase.co",
            ["Database:Provider"] = "PostgreSql", ["Database:Schema"] = "public", ["Database:MaxPoolSize"] = "2",
            ["Database:Host"] = "offline.pooler.supabase.com", ["Database:Username"] = $"glam_v2_backend.{Program.Project}",
            ["Database:Password"] = poison, ["Database:Name"] = "postgres", ["Database:Port"] = "5432"
        };
        IConfiguration Config(Dictionary<string, string?> input) => new ConfigurationBuilder().AddInMemoryCollection(input).Build();
        var settings = Program.Validate(Config(values));
        Check(settings.SslMode == SslMode.VerifyFull && settings.Timeout == 15 && settings.CommandTimeout == 30);
        Check(settings.Pooling && settings.MinPoolSize == 0 && settings.MaxPoolSize == 2 && !settings.Multiplexing);
        Check(!settings.IncludeErrorDetail && !settings.PersistSecurityInfo && settings.Password == poison);
        Check(settings.GssEncryptionMode == new NpgsqlConnectionStringBuilder().GssEncryptionMode);
        Check(settings.SslNegotiation == new NpgsqlConnectionStringBuilder().SslNegotiation);
        var original = settings.ConnectionString;
        var disabled = Program.DisableGssForDiagnostic(settings);
        Check(disabled.GssEncryptionMode == GssEncryptionMode.Disable && !ReferenceEquals(settings, disabled));
        Check(settings.ConnectionString == original && settings.GssEncryptionMode == new NpgsqlConnectionStringBuilder().GssEncryptionMode);
        Check(disabled.SslMode == SslMode.VerifyFull && disabled.SslNegotiation == settings.SslNegotiation);
        Check(disabled.Timeout == 15 && disabled.CommandTimeout == 30 && disabled.MinPoolSize == 0 && disabled.MaxPoolSize == 2);
        Check(disabled.Host == settings.Host && disabled.Username == settings.Username && disabled.Password == settings.Password);
        Check(disabled.RootCertificate == settings.RootCertificate && disabled.Database == settings.Database && disabled.Port == settings.Port);
        var explicitRoot = new NpgsqlConnectionStringBuilder(original) { RootCertificate = "synthetic-private-root-file" };
        Check(Program.DisableGssForDiagnostic(explicitRoot).RootCertificate == explicitRoot.RootCertificate);
        Check(Program.DisableGssForDiagnostic(disabled).EquivalentTo(disabled));
        using (var wire = new ReplyStream(new byte[] { (byte)'S' }))
        {
            ProtocolProbe.Exchange(wire, default).GetAwaiter().GetResult();
            Check(wire.Sent.SequenceEqual(new byte[] { 0, 0, 0, 8, 4, 210, 22, 47 }));
        }
        foreach (var reply in new[] { Array.Empty<byte>(), new byte[] { (byte)'N' }, new byte[] { 0 } })
        {
            using var wire = new ReplyStream(reply);
            var rejected = false;
            try { ProtocolProbe.Exchange(wire, default).GetAwaiter().GetResult(); }
            catch (Exception error) when (error is AuthenticationException or EndOfStreamException) { rejected = true; }
            Check(rejected);
        }
        using (var canceled = new CancellationTokenSource())
        using (var wire = new ReplyStream(new byte[] { (byte)'S' }))
        {
            canceled.Cancel();
            var rejected = false;
            try { ProtocolProbe.Exchange(wire, canceled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected && wire.Sent.Count == 0);
        }
        var options = ProtocolProbe.Options(settings);
        Check(options.TargetHost == settings.Host && options.RemoteCertificateValidationCallback == null &&
            options.CertificateChainPolicy == null);
        var weak = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { SslMode = SslMode.Require };
        var weakRejected = false;
        try { ProtocolProbe.Options(weak); } catch (InvalidOperationException) { weakRejected = true; }
        Check(weakRejected);
        foreach (var key in new[] { "Deployment:Profile", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT",
            "Deployment:V2ProjectId", "Database:ProjectId", "Supabase:Url", "Database:Provider", "Database:Schema",
            "Database:Username", "Database:Host", "Database:Name", "Database:Port", "Database:MaxPoolSize", "Database:Password",
            "ConnectionStrings:MakeupDatabase" })
        {
            var rejected = false;
            try { Program.Validate(Config(new(values) { [key] = key == "Database:Password" ? "" : "wrong" })); }
            catch (Exception) { rejected = true; }
            Check(rejected);
        }
        Check(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CustomerAuthReadinessSql.Query.Replace("\r\n", "\n"))))
            == "2AB16C17D467F5C04EBBAFF706D5DCB9F63D1C85164D01E5AF283460CD7AAD74");
        Check(CustomerAuthReadinessSql.Query.TrimStart().StartsWith("SELECT ") && !CustomerAuthReadinessSql.Query.Contains(';'));
        Check(Program.IdentityQuery.Contains("session_user='glam_v2_backend'") &&
            Program.IdentityQuery.Contains("transaction_read_only") && !Program.IdentityQuery.Contains(';'));
        foreach (var error in new Exception[]
        {
            new InvalidOperationException(poison), new NpgsqlException(poison, new TimeoutException(poison)),
            new NpgsqlException(poison, new AuthenticationException(poison)),
            new OperationCanceledException(poison), new PostgresException(poison, "ERROR", "ERROR", "28P01"),
            new PostgresException(poison, "ERROR", "ERROR", poison)
        })
        {
            var record = DiagnosticTrace.Record(Phase.ColdPoolAcquire, Outcome.Failed, 15, 10, error);
            Check(!record.Contains(poison) && record.Contains("|duration_ms=10|"));
        }
        Check(DiagnosticTrace.Record(Phase.ColdPoolAcquire, Outcome.Failed, 0, 0,
            new OperationCanceledException(poison)).Contains("|category=canceled|"));
        Check(DiagnosticTrace.Record(Phase.ColdPoolAcquire, Outcome.Failed, 0, 0,
            new TimeoutException(poison)).Contains("|category=timeout|"));

        using var output = new StringWriter();
        var trace = new DiagnosticTrace(output);
        NetworkObserver.Observe(trace, poison, poison);
        NetworkObserver.Observe(trace, "System.Net.Security", "HandshakeStop");
        Check(output.ToString() == "");
        foreach (var (source, start, stop) in new[]
        {
            ("System.Net.NameResolution", "ResolutionStart", "ResolutionStop"),
            ("System.Net.Sockets", "ConnectStart", "ConnectStop"),
            ("System.Net.Security", "HandshakeStart", "HandshakeStop")
        })
        {
            NetworkObserver.Observe(trace, source, start);
            NetworkObserver.Observe(trace, source, stop);
            Check(output.ToString().Contains("|status=Passed|"));
            output.GetStringBuilder().Clear();
        }
        trace.End(Phase.PostTlsLoginOrBootstrap, Outcome.Passed);
        Check(trace.SuccessfulTlsHandshakes == 1);
        output.GetStringBuilder().Clear();
        NetworkObserver.Observe(trace, "System.Net.Security", "HandshakeStart");
        NetworkObserver.Observe(trace, "System.Net.Security", "HandshakeFailed");
        NetworkObserver.Observe(trace, "System.Net.Security", "HandshakeStop");
        Check(!output.ToString().Contains("|status=Passed|") && !output.ToString().Contains("PostTlsLoginOrBootstrap"));
        Check(trace.SuccessfulTlsHandshakes == 1);
        output.GetStringBuilder().Clear();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        NetworkObserver.Observe(trace, "System.Net.Sockets", "ConnectStart", a);
        NetworkObserver.Observe(trace, "System.Net.Sockets", "ConnectStart", b);
        NetworkObserver.Observe(trace, "System.Net.Sockets", "ConnectFailed", a);
        NetworkObserver.Observe(trace, "System.Net.Sockets", "ConnectStop", b);
        Check(output.ToString().Split("|status=Passed|").Length == 2);
        Check(!output.ToString().Contains(a.ToString()) && !output.ToString().Contains(b.ToString()));
        output.GetStringBuilder().Clear();
        using (var factory = new DriverObserver(trace))
        {
            var logger = factory.CreateLogger("Npgsql.Connection");
            logger.Log(LogLevel.Trace, new EventId(1110), poison, new Exception(poison),
                (_, _) => throw new InvalidOperationException("Formatter must never be called."));
            Check(trace.PhysicalOpens == 1);
            logger.Log(LogLevel.Trace, new EventId(1111), poison, null, (_, _) => poison);
            Check(!output.ToString().Contains(poison));
        }
        var methods = typeof(NpgsqlConnection).Assembly.GetType("Npgsql.LogMessages")!
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var (name, id) in new[] { ("OpeningPhysicalConnection", 1110), ("OpenedPhysicalConnection", 1111), ("ExecutingInternalCommand", 2003) })
            Check(methods.Single(m => m.Name == name).GetCustomAttribute<LoggerMessageAttribute>()?.EventId == id);
        foreach (var (assembly, source, names) in new[]
        {
            (typeof(System.Net.Dns).Assembly, "System.Net.NameResolution", new[] { "ResolutionStart", "ResolutionStop", "ResolutionFailed" }),
            (typeof(System.Net.Sockets.Socket).Assembly, "System.Net.Sockets", new[] { "ConnectStart", "ConnectStop", "ConnectFailed" }),
            (typeof(System.Net.Security.SslStream).Assembly, "System.Net.Security", new[] { "HandshakeStart", "HandshakeStop", "HandshakeFailed" })
        })
        {
            var telemetry = assembly.GetTypes().Single(t => t.IsAssignableTo(typeof(EventSource)) &&
                t.GetCustomAttribute<EventSourceAttribute>()?.Name == source);
            foreach (var name in names)
                Check(telemetry.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(m => m.Name == name && m.GetCustomAttribute<EventAttribute>() != null));
        }
        trace.Begin(Phase.ReadinessQuery);
        trace.Fail(new InvalidOperationException(poison));
        Check(output.ToString().Contains("stage=ReadinessQuery|status=Failed") && !output.ToString().Contains(poison));
        output.GetStringBuilder().Clear();
        trace.Begin(Phase.ColdPoolAcquire);
        trace.Begin(Phase.PoolDispatch, main: false);
        DriverObserver.Observe(trace, "Npgsql.Connection", 1110);
        Check(output.ToString().Contains("stage=PoolDispatch|status=Passed"));
        trace.Fail(new TimeoutException(poison));
        Check(output.ToString().Contains("stage=PhysicalOpen|status=Unfinished"));
        Console.WriteLine($"PASS: {checks} offline configuration/query/observer/redaction checks; no owner configuration, sockets, host or database accessed.");
    }

    private sealed class ReplyStream(byte[] reply) : MemoryStream(reply, writable: false)
    {
        internal List<byte> Sent { get; } = new();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.AddRange(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
