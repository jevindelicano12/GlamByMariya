using System.Net;
using System.Net.Sockets;

// This runner is deliberately disarmed. Preflight is offline and cannot open
// connections. Execution stays blocked until a separately reviewed isolation
// authority is provisioned and the source-level kill switch is changed by an
// explicitly authorized follow-up.
if (args.Length == 1 && args[0] == "--preflight")
{
    var results = RunnerSafety.PreflightCases();
    foreach (var (name, passed) in results)
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} (offline preflight): {name}");
    Console.WriteLine($"Offline safeguards: {results.Count(x => x.Passed)} passed; {results.Count(x => !x.Passed)} failed.");
    Console.WriteLine("No configuration, environment variable, network endpoint, or database was read or contacted.");
    return results.All(x => x.Passed) ? 0 : 1;
}

if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Offline only: use --preflight. Runtime execution is not armed.");
    return 0;
}

if (!args.Contains("--execute", StringComparer.Ordinal))
{
    Console.Error.WriteLine("Refused: explicit --execute opt-in is required; use --preflight for offline checks.");
    return 2;
}

if (!RunnerSafety.IsExecutionArmed())
{
    Console.Error.WriteLine("Refused: runtime execution is disabled pending independently approved disposable-environment attestation.");
    return 3;
}

// Deliberately unreachable while ExecutionArmed is false. No app configuration
// or environment fallback is available in this project.
var parsed = RunnerSafety.ParseExplicitOptions(args);
if (!parsed.Accepted || parsed.Options is null)
{
    Console.Error.WriteLine("Refused: " + parsed.Error);
    return 4;
}

if (!RunnerSafety.HasApprovedIsolationAuthority(parsed.Options))
{
    Console.Error.WriteLine("Refused: no independently pinned isolation authority is configured.");
    return 5;
}

var password = ReadSecretLine();
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("Refused: the one-time stdin password was empty.");
    return 6;
}
return await RuntimeIntegrationSuite.RunAsync(parsed.Options with { Password = password });

static string? ReadSecretLine()
{
    if (Console.IsInputRedirected) return Console.ReadLine();
    Console.Error.Write("Disposable test database password (hidden): ");
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
            continue;
        }
        if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
    }
    Console.Error.WriteLine();
    return new string(chars.ToArray());
}

static class RunnerSafety
{
    public static bool IsExecutionArmed() => false;
    private const string ExpectedDatabase = "glam_phase217g_disposable";
    private const string ExpectedRole = "glam_phase217g_runner";
    private const int DedicatedPort = 56433;
    // Deliberately empty: no independently approved endpoint/attestation has
    // been provisioned for this repository. Never add a user-supplied endpoint.
    private static readonly HashSet<IPAddress> ApprovedEndpointAddresses = [];
    private static readonly HashSet<string> ForbiddenHostFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        "supabase", "render.com", "render.app", "localhost", "127.0.0.1", "::1"
    };

    public sealed record ExplicitOptions(IPAddress Host, int Port, string Database, string Username,
        string IsolationReceiptPath, string? Password);
    public sealed record ParseResult(bool Accepted, ExplicitOptions? Options, string Error);

    public static IReadOnlyList<(string Name, bool Passed)> PreflightCases()
    {
        var results = new List<(string, bool)>();
        void Test(string name, bool value) => results.Add((name, value));

        Test("empty invocation has no explicit configuration", !ParseExplicitOptions([]).Accepted);
        Test("connection strings and app-config options are rejected", !ParseExplicitOptions(["--connection-string", "Host=x"]).Accepted);
        Test("known blocked ports are rejected", !ValidateFields("192.0.2.10", 5432, ExpectedDatabase, ExpectedRole, "receipt").Accepted
            && !ValidateFields("192.0.2.10", 56432, ExpectedDatabase, ExpectedRole, "receipt").Accepted);
        Test("only the designated non-default port is accepted", !ValidateFields("192.0.2.10", 5433, ExpectedDatabase, ExpectedRole, "receipt").Accepted);
        Test("loopback and unspecified hosts are rejected", !ValidateFields("127.0.0.1", DedicatedPort, ExpectedDatabase, ExpectedRole, "receipt").Accepted
            && !ValidateFields("0.0.0.0", DedicatedPort, ExpectedDatabase, ExpectedRole, "receipt").Accepted);
        Test("cloud and unapproved hostnames are rejected", !ValidateFields("project.supabase.co", DedicatedPort, ExpectedDatabase, ExpectedRole, "receipt").Accepted
            && !ValidateFields("my-service.onrender.com", DedicatedPort, ExpectedDatabase, ExpectedRole, "receipt").Accepted
            && !ValidateFields("pg.example.invalid", DedicatedPort, ExpectedDatabase, ExpectedRole, "receipt").Accepted);
        Test("dedicated synthetic database and role are mandatory", !ValidateFields("192.0.2.10", DedicatedPort, "postgres", ExpectedRole, "receipt").Accepted
            && !ValidateFields("192.0.2.10", DedicatedPort, ExpectedDatabase, "postgres", "receipt").Accepted);
        Test("explicit isolation evidence is mandatory", !ValidateFields("192.0.2.10", DedicatedPort, ExpectedDatabase, ExpectedRole, "").Accepted);
        Test("execution is disarmed and no endpoint is approved", !IsExecutionArmed() && ApprovedEndpointAddresses.Count == 0);
        Test("independent isolation authority is currently unavailable", !HasApprovedIsolationAuthority(null));
        Test("private address still requires an independently approved allow-list entry",
            !ValidateFields("198.51.100.10", DedicatedPort, ExpectedDatabase, ExpectedRole, "receipt").Accepted
            && !ValidateFields("192.168.56.10", DedicatedPort, ExpectedDatabase, ExpectedRole, "receipt").Accepted);
        return results;
    }

    public static ParseResult ParseExplicitOptions(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--execute" or "--preflight") continue;
            if (args[index] is not ("--host" or "--port" or "--database" or "--username" or "--isolation-receipt" or "--password-stdin"))
                return new(false, null, "Unknown option; connection strings and configuration fallbacks are unsupported.");
            if (args[index] == "--password-stdin")
            {
                if (values.ContainsKey("--password-stdin")) return new(false, null, "Duplicate option.");
                values.Add("--password-stdin", "required");
                continue;
            }
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                return new(false, null, "Every connection setting must be supplied explicitly.");
            if (!values.TryAdd(args[index - 1], args[index])) return new(false, null, "Duplicate option.");
        }

        foreach (var key in new[] { "--host", "--port", "--database", "--username", "--isolation-receipt", "--password-stdin" })
            if (!values.ContainsKey(key)) return new(false, null, "Missing explicit option " + key + ".");
        if (!int.TryParse(values["--port"], out var port)) return new(false, null, "Port must be numeric.");
        var validated = ValidateFields(values["--host"], port, values["--database"], values["--username"], values["--isolation-receipt"]);
        if (!validated.Accepted) return validated;

        return validated;
    }

    public static bool HasApprovedIsolationAuthority(ExplicitOptions? options) =>
        IsExecutionArmed() && options is not null && ApprovedEndpointAddresses.Contains(options.Host);

    private static ParseResult ValidateFields(string host, int port, string database, string username, string receiptPath)
    {
        if (port is 5432 or 56432 || port != DedicatedPort)
            return new(false, null, "Only the separately approved dedicated port is permitted.");
        if (database != ExpectedDatabase || username != ExpectedRole)
            return new(false, null, "Only the dedicated synthetic database and role are permitted.");
        if (string.IsNullOrWhiteSpace(receiptPath)) return new(false, null, "Independent isolation receipt is required.");
        if (ForbiddenHostFragments.Any(host.Contains)) return new(false, null, "Forbidden or local endpoint.");
        if (!IPAddress.TryParse(host, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || IPAddress.IsLoopback(address) || !IsPrivate(address))
            return new(false, null, "An explicitly allow-listed private IP endpoint is required; DNS names are rejected.");
        if (!ApprovedEndpointAddresses.Contains(address))
            return new(false, null, "No independently approved disposable endpoint is allow-listed.");

        // This validates only an endpoint shape. It does NOT prove isolation.
        // No operator-provided receipt or server marker can override the closed
        // source-level execution gate above.
        return new(true, new(address, port, database, username, receiptPath, null), string.Empty);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}
