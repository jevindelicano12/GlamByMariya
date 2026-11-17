using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlamByMariya.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal static class Program
{
    internal const string Project = "hqllkbjhufhvyooacajy";
    internal const string IdentityQuery = """
        SELECT current_user='glam_v2_backend' AND session_user='glam_v2_backend'
          AND current_database()='postgres'
          AND current_setting('transaction_read_only')='on'
        """;

    private static async Task<int> Main(string[] args)
    {
        var trace = new DiagnosticTrace(Console.Out);
        try
        {
            // Offline modes branch before private configuration or network APIs.
            if (args is ["--self-test"]) { OfflineChecks.Run(); return 0; }
            if (args is ["--driver-review"]) { DriverReview.Run(); return 0; }
            if (args is ["--local-tls-test"]) { await LocalTlsChecks.Run(); return 0; }
            if (args is ["--supervision-self-test"]) { await SupervisionChecks.Run(); return 0; }
            if (args is ["--launcher-self-test"]) { await LauncherChecks.Run(); return 0; }
            if (args is ["--stage-self-test"]) { await StageChecks.Run(); return 0; }
            if (args is ["--supervision-fixture", _, _])
                return await SupervisionChecks.Fixture(args[1], args[2]);
            if (args is ["--supervise-owner-approved", _, _])
                return await DiagnosticSupervisor.Run(args[1], args[2]);
            var consolidated = args is ["--consolidated-owner-approved"];
            var disableGss = args is ["--check-local", "--disable-gss"] or ["--run-once-owner-approved", "--disable-gss"];
            if (!consolidated && !disableGss && args is not (["--check-local"] or ["--run-once-owner-approved"]))
            {
                trace.Mark(Phase.Configuration, Outcome.Refused);
                return 2;
            }
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows diagnostic supervision required.");
            var localOnly = args[0] == "--check-local";
            var deadline = localOnly ? RunDeadline.After(75000) : RunDeadline.FromSupervisor();
            using var budget = new CancellationTokenSource(RunDeadline.Remaining(deadline));
            using var cancelSignal = localOnly ? null :
                EventWaitHandle.OpenExisting(Environment.GetEnvironmentVariable(RunDeadline.CancelVariable)
                    ?? throw new InvalidOperationException("Missing supervisor cancellation event."));
            CheckBudget(deadline, budget.Token);
            var settings = LoadPrivateConfiguration();
            CheckBudget(deadline, budget.Token);
            if (disableGss || consolidated) settings = DisableGssForDiagnostic(settings);
            PrintSettings(settings);
            if (args[0] == "--check-local") return 0;
            using var observer = new NetworkObserver(trace);
            using var logger = new DriverObserver(trace);
            return await RunStages(consolidated,
                () => ProtocolProbe.Run(settings, trace, budget.Token, deadline),
                () => VerifyManaged(settings, trace, logger, budget.Token, deadline),
                () => {
                    CheckBudget(deadline, budget.Token);
                    Require(cancelSignal == null || !cancelSignal.WaitOne(0), "Supervisor canceled the sequence.");
                }, trace);
        }
        catch (Exception error)
        {
            trace.Fail(error);
            // No retry, raw exception, formatter, connection string or secret output.
            return 1;
        }
    }

    internal static async Task<int> RunStages(bool consolidated, Func<Task> protocol, Func<Task> managed,
        Action checkSafety, DiagnosticTrace trace)
    {
        var failed = false;
        checkSafety();
        if (consolidated)
        {
            try { await protocol(); }
            catch (ProtocolFailureException error)
            {
                trace.Fail(error.InnerException!);
                failed = true;
            }
        }
        // Probe success is not an authentication result; managed opening must verify its own TLS and identity.
        checkSafety();
        await managed();
        checkSafety();
        trace.Mark(Phase.Complete, failed ? Outcome.Failed : Outcome.Passed);
        return failed ? 1 : 0;
    }

    private static async Task VerifyManaged(NpgsqlConnectionStringBuilder settings, DiagnosticTrace trace,
        DriverObserver logger, CancellationToken ct, long deadline)
    {
        CheckBudget(deadline, ct);
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString) { Name = "GlamV2ReadOnlyDiagnostic" };
        builder.UseLoggerFactory(logger).EnableParameterLogging(false);
        await using var source = builder.Build();
        trace.Begin(Phase.ColdPoolAcquire);
        CheckBudget(deadline, ct);
        trace.Begin(Phase.PoolDispatch, main: false);
        var priorTls = trace.SuccessfulTlsHandshakes;
        int physicalId;
        await using (var cold = await source.OpenConnectionAsync(ct))
        {
            Require(trace.SuccessfulTlsHandshakes > priorTls && trace.PhysicalOpens == 1,
                "Verified TLS/physical-open evidence was not observed exactly once.");
            physicalId = cold.ProcessID;
            trace.End(Phase.PostTlsLoginOrBootstrap, Outcome.Passed);
            trace.End(Phase.ColdPoolAcquire, Outcome.Passed);
        }
        // Exactly one sequential warm borrow; no concurrent holder or second source.
        var opens = trace.PhysicalOpens;
        trace.Begin(Phase.WarmPoolAcquire);
        CheckBudget(deadline, ct);
        await using var connection = await source.OpenConnectionAsync(ct);
        Require(connection.ProcessID == physicalId &&
            trace.PhysicalOpens == opens, "Warm pool did not reuse the same physical connection.");
        trace.End(Phase.WarmPoolAcquire, Outcome.Passed);

        trace.Begin(Phase.ReadOnlyBegin);
        CheckBudget(deadline, ct);
        await Execute(connection, "BEGIN READ ONLY", ct);
        trace.End(Phase.ReadOnlyBegin, Outcome.Passed);
        trace.Begin(Phase.RestrictedIdentity);
        CheckBudget(deadline, ct);
        await using (var command = new NpgsqlCommand(IdentityQuery, connection))
            Require(await command.ExecuteScalarAsync(ct) is true, "Restricted identity/read-only guard failed.");
        trace.End(Phase.RestrictedIdentity, Outcome.Passed);
        trace.Begin(Phase.ReadinessQuery);
        CheckBudget(deadline, ct);
        await using (var command = new NpgsqlCommand(CustomerAuthReadinessSql.Query, connection))
            Require(await command.ExecuteScalarAsync(ct) is true, "Readiness predicate failed.");
        trace.End(Phase.ReadinessQuery, Outcome.Passed);
        trace.Begin(Phase.Rollback);
        CheckBudget(deadline, ct);
        await Execute(connection, "ROLLBACK", ct);
        trace.End(Phase.Rollback, Outcome.Passed);
    }

    internal static void CheckBudget(long deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RunDeadline.Remaining(deadline);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static NpgsqlConnectionStringBuilder DisableGssForDiagnostic(NpgsqlConnectionStringBuilder original)
    {
        // Clone only in this tool; never alter the shared normalizer or owner file.
        var changed = new NpgsqlConnectionStringBuilder(original.ConnectionString) { GssEncryptionMode = GssEncryptionMode.Disable };
        var before = new NpgsqlConnectionStringBuilder(original.ConnectionString);
        var after = new NpgsqlConnectionStringBuilder(changed.ConnectionString);
        before.Remove("GSS Encryption Mode");
        after.Remove("GSS Encryption Mode");
        Require(changed.GssEncryptionMode == GssEncryptionMode.Disable && before.EquivalentTo(after),
            "Diagnostic changed a setting other than GSS encryption.");
        return changed;
    }

    internal static NpgsqlConnectionStringBuilder Validate(IConfiguration configuration)
    {
        Require(configuration["Deployment:Profile"] == "v2-customer-auth-test" &&
            configuration["DOTNET_ENVIRONMENT"] == "V2CustomerAuthTest" &&
            configuration["ASPNETCORE_ENVIRONMENT"] == "V2CustomerAuthTest" &&
            configuration["Deployment:V2ProjectId"] == Project &&
            configuration["Database:ProjectId"] == Project &&
            configuration["Supabase:Url"] == $"https://{Project}.supabase.co" &&
            configuration["Database:Provider"] == "PostgreSql" &&
            configuration["Database:Schema"] == "public" &&
            string.IsNullOrEmpty(configuration["ConnectionStrings:MakeupDatabase"]),
            "Configuration pins failed.");
        var settings = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(configuration)
            ?? throw new InvalidOperationException("No restricted connection."));
        Require(settings.Username == $"glam_v2_backend.{Project}" && settings.Port == 5432 &&
            settings.Database == "postgres" &&
            settings.Host is { } host && Regex.IsMatch(host, @"^[a-z0-9.-]+\.pooler\.supabase\.com$", RegexOptions.IgnoreCase) &&
            settings.SslMode == SslMode.VerifyFull && settings.Pooling && settings.MinPoolSize == 0 &&
            settings.MaxPoolSize == 2 && settings.Timeout == 15 && settings.CommandTimeout == 30 &&
            !settings.IncludeErrorDetail && !settings.PersistSecurityInfo && !settings.Multiplexing,
            "Effective connection settings failed.");
        return settings;
    }

    private static NpgsqlConnectionStringBuilder LoadPrivateConfiguration()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows owner configuration required.");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlamByMariyaV2", "CustomerAuthOwner");
        var path = Path.Combine(root, "config", "secrets.json");
        for (DirectoryInfo? parent = new(root); parent != null; parent = parent.Parent)
            Require(!parent.Attributes.HasFlag(FileAttributes.ReparsePoint), "Reparse ancestor.");
        foreach (var item in new[] { root, Path.GetDirectoryName(path)!, path }) CheckPrivateAcl(item);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            Require(property.Value.ValueKind == JsonValueKind.String, "Non-string configuration.");
            Require(values.TryAdd(property.Name.Replace("__", ":"), property.Value.GetString()), "Duplicate configuration.");
        }
        return Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [SupportedOSPlatform("windows")]
    private static void CheckPrivateAcl(string path)
    {
        Require(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "Reparse path.");
        FileSystemSecurity acl = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var owner = WindowsIdentity.GetCurrent().User?.Value;
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Require(rules.Length == 2 && rules.All(rule =>
            (rule.IdentityReference.Value == owner || rule.IdentityReference.Value == "S-1-5-18") &&
            rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl), "Private ACL required.");
    }

    private static void PrintSettings(NpgsqlConnectionStringBuilder settings)
    {
        // Only non-secret enums/numbers; no host, qualified username, paths or values.
        Console.WriteLine($"DBCONFIG|project_pins=Passed|tls={settings.SslMode}|timeout_s={settings.Timeout}" +
            $"|command_timeout_s={settings.CommandTimeout}|pool_min={settings.MinPoolSize}|pool_max={settings.MaxPoolSize}" +
            $"|gss={settings.GssEncryptionMode}|ssl_negotiation={settings.SslNegotiation}" +
            $"|root={(string.IsNullOrWhiteSpace(settings.RootCertificate) ? "SystemTrust" : "ExplicitFile")}");
    }
}
