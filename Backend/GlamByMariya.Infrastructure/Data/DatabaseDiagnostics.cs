using System.Net.Sockets;
using System.Security.Authentication;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Whitelisted categories only: raw provider messages can include server details
// or submitted SQL. Never print exception.ToString(), connection strings or keys.
public static class DatabaseDiagnostics
{
    public static bool IsSessionPoolExhausted(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is PostgresException postgres && postgres.SqlState == "XX000"
                && postgres.MessageText.Contains("EMAXCONNSESSION", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string Describe(Exception error)
    {
        if (IsSessionPoolExhausted(error))
            return "Supabase session connection capacity is exhausted. Check the app's shared pool budget, other instances and database clients; no automatic retry loop was added.";
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                if (postgres.MessageText.Contains("Tenant or user not found", StringComparison.OrdinalIgnoreCase))
                    return "Supabase could not locate this project/user at the supplied pooler endpoint. Verify the session pooler host/region and project-qualified username in the project's Connect dialog.";
                return postgres.SqlState switch
                {
                    "28P01" or "28000" => "Database authentication failed. Verify the database password and username; if the password was reset, update local User Secrets. No data changes were attempted by connection inspection.",
                    "3D000" => "The configured PostgreSQL database does not exist. Verify the database name in the Connect dialog.",
                    "42501" => "Database access was denied by role privileges. Verify read access before planning migrations or application writes.",
                    "53300" => "The database connection limit has been reached. Check the project's pooler and connection settings.",
                    "57P03" => "The database is temporarily unavailable or starting. Check the project's status in Supabase.",
                    _ => $"Database operation failed (PostgreSQL code {postgres.SqlState}). Inspect the schema and role permissions without sharing credentials."
                };
            }
            if (current is AuthenticationException)
                return "The TLS handshake or certificate validation failed. Verify the Supabase endpoint and certificate trust; certificate verification has not been disabled.";
            if (current is SocketException socket)
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData => "The database hostname could not be resolved. Verify the exact host in the Supabase Connect dialog and your DNS/network access.",
                    SocketError.NetworkUnreachable or SocketError.AddressFamilyNotSupported => "The database network address is unreachable. For an IPv4-only network, use the session pooler details from Supabase Connect.",
                    SocketError.ConnectionRefused => "The database host refused the connection. Verify the endpoint, port, project status and network restrictions.",
                    _ => $"A database network connection failed ({socket.SocketErrorCode}). Verify network access and the exact Supabase endpoint."
                };
            if (current is TimeoutException)
                return "The database connection or command timed out. Check the endpoint, network restrictions and Supabase project status.";
        }
        if (error is InvalidOperationException && error.InnerException == null)
            return error.Message;
        return "Database operation failed. Verify secure credentials, network access, TLS, schema mappings and role permissions. No demo fallback was used.";
    }
}
