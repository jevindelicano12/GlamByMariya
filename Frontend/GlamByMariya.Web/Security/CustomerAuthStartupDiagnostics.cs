using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Npgsql;

namespace GlamByMariya.Web.Security;

internal enum CustomerAuthStartupStage
{
    Configuration, AuthenticationRegistration, DatabaseConfiguration, CertificateSetup,
    DataProtectionRegistration, ServiceRegistration, HostBuild, RestrictedDatabaseReadiness,
    DataProtectionWarmup, HttpsBinding, Running
}

internal static class CustomerAuthStartupDiagnostics
{
    // Only fixed categories/types/codes; never exception messages, paths or SQL.
    internal static string Describe(CustomerAuthStartupStage stage, Exception error)
    {
        var category = "startup-failure";
        var code = "None";
        var cause = error;
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            cause = current;
            if (current is PostgresException postgres)
            {
                category = postgres.SqlState switch
                {
                    "28P01" or "28000" => "database-authentication",
                    "42501" => "database-access-denied",
                    "42P01" or "42703" or "42883" => "database-schema-or-query",
                    "53300" => "database-capacity",
                    _ => "database-query"
                };
                code = postgres.SqlState is "28P01" or "28000" or "42501" or "42P01" or
                    "42703" or "42883" or "53300" or "3D000" or "57P03" or "XX000"
                    ? postgres.SqlState : "Other";
                break;
            }
            if (current is AuthenticationException) { category = "database-or-https-tls"; break; }
            if (current is SocketException socket)
            {
                category = "network";
                code = socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or
                    SocketError.NetworkUnreachable or SocketError.ConnectionRefused or SocketError.TimedOut
                    ? socket.SocketErrorCode.ToString() : "Other";
                break;
            }
            if (current is TimeoutException) { category = "timeout"; break; }
            if (current is OperationCanceledException) { category = "canceled"; break; }
            if (current is CryptographicException) { category = "cryptography"; break; }
            if (current is UnauthorizedAccessException) { category = "local-access-denied"; break; }
        }
        return $"V2AUTH|stage={stage}|category={category}|type={Type(error)}|cause={Type(cause)}|code={code}";
    }

    private static string Type(Exception error) => error switch
    {
        PostgresException => nameof(PostgresException),
        NpgsqlException => nameof(NpgsqlException),
        AuthenticationException => nameof(AuthenticationException),
        SocketException => nameof(SocketException),
        TimeoutException => nameof(TimeoutException),
        CryptographicException => nameof(CryptographicException),
        UnauthorizedAccessException => nameof(UnauthorizedAccessException),
        DirectoryNotFoundException => nameof(DirectoryNotFoundException),
        FileNotFoundException => nameof(FileNotFoundException),
        IOException => nameof(IOException),
        InvalidOperationException => nameof(InvalidOperationException),
        ArgumentException => nameof(ArgumentException),
        OperationCanceledException => nameof(OperationCanceledException),
        _ => nameof(Exception)
    };
}
