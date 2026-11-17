using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using GlamByMariya.Web.Security;
using Npgsql;

internal static class CustomerStartupDiagnosticChecks
{
    internal static void Run()
    {
        const string privateMarker = "PRIVATE-host-email-password-token-certificate-SQL";
        var checks = 0;
        foreach (var (error, category, cause, code) in new (Exception, string, string, string)[]
        {
            (new PostgresException(privateMarker, "ERROR", "ERROR", "42501"), "database-access-denied", "PostgresException", "42501"),
            (new PostgresException(privateMarker, "ERROR", "ERROR", "28P01"), "database-authentication", "PostgresException", "28P01"),
            (new PostgresException(privateMarker, "ERROR", "ERROR", "42883"), "database-schema-or-query", "PostgresException", "42883"),
            (new PostgresException(privateMarker, "ERROR", "ERROR", privateMarker), "database-query", "PostgresException", "Other"),
            (new NpgsqlException(privateMarker, new AuthenticationException(privateMarker)), "database-or-https-tls", "AuthenticationException", "None"),
            (new NpgsqlException(privateMarker, new SocketException((int)SocketError.ConnectionRefused)), "network", "SocketException", "ConnectionRefused"),
            (new InvalidOperationException(privateMarker, new TimeoutException(privateMarker)), "timeout", "TimeoutException", "None"),
            (new CryptographicException(privateMarker), "cryptography", "CryptographicException", "None"),
            (new UnauthorizedAccessException(privateMarker), "local-access-denied", "UnauthorizedAccessException", "None"),
            (new InvalidOperationException(privateMarker), "startup-failure", "InvalidOperationException", "None")
        })
        {
            var result = CustomerAuthStartupDiagnostics.Describe(CustomerAuthStartupStage.RestrictedDatabaseReadiness, error);
            if (result.Contains(privateMarker) || !result.Contains("|category=" + category + "|") ||
                !result.Contains("|cause=" + cause + "|") || !result.EndsWith("|code=" + code))
                throw new InvalidOperationException("Sanitized startup diagnostic check failed.");
            checks++;
        }
        foreach (var stage in Enum.GetValues<CustomerAuthStartupStage>())
        {
            var result = CustomerAuthStartupDiagnostics.Describe(stage, new Exception(privateMarker));
            if (!result.StartsWith("V2AUTH|stage=" + stage + "|") || result.Contains(privateMarker))
                throw new InvalidOperationException("Startup stage diagnostic check failed.");
            checks++;
        }
        Console.WriteLine($"PASS: {checks} synthetic startup diagnostic/redaction checks; no host, configuration or database accessed.");
    }
}
