using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

public static class DatabaseConfiguration
{
    public static string? ConnectionString(IConfiguration config)
    {
        try { return BuildConnectionString(config); }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            // Provider/URI parser diagnostics must never echo an entered secret.
            throw new InvalidOperationException("Invalid database connection configuration. Copy the complete URI or Npgsql connection string from the target project's Connect dialog and store it securely.");
        }
    }
    private static string? BuildConnectionString(IConfiguration config)
    {
        var supplied=config.GetConnectionString("MakeupDatabase");
        NpgsqlConnectionStringBuilder connection;
        if(!string.IsNullOrWhiteSpace(supplied))
        {
            if(supplied.StartsWith("postgresql://",StringComparison.OrdinalIgnoreCase)
                || supplied.StartsWith("postgres://",StringComparison.OrdinalIgnoreCase)
                || supplied.StartsWith("tcp://",StringComparison.OrdinalIgnoreCase))
            {
                // Some hosting dashboards display a transport endpoint as
                // tcp://host:port. Treat it as a PostgreSQL URI only when it
                // includes PostgreSQL URI credentials/path; never pass the
                // literal tcp:// prefix to Npgsql as the Host value.
                var uri=new Uri(supplied); var credentials=uri.UserInfo.Split(':',2);
                if(credentials.Length!=2) throw new InvalidOperationException("The PostgreSQL URI must include a username and password.");
                if(string.IsNullOrWhiteSpace(uri.Host) || string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
                    throw new InvalidOperationException("The database URI must include a host and database name.");
                connection=new() { Host=uri.Host,Port=uri.IsDefaultPort?5432:uri.Port,Database=Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
                    Username=Uri.UnescapeDataString(credentials[0]),Password=Uri.UnescapeDataString(credentials[1]) };
                if(!string.IsNullOrEmpty(uri.Query)) throw new InvalidOperationException("Use an Npgsql key/value connection string for additional connection options.");
            }
            else connection=new(supplied);
        }
        else
        {
            var password=config["Database:Password"];
            if(string.IsNullOrWhiteSpace(password)) return null;
            connection=new() { Host=config["Database:Host"]??"",Port=config.GetValue("Database:Port",5432),
                Database=config["Database:Name"]??"postgres",Username=config["Database:Username"]??"postgres",Password=password };
        }
        // Also repair a transport prefix mistakenly entered in an Npgsql
        // key/value Host field (Host=tcp://pooler:5432). Keep the independently
        // configured Port when the host URI omits one.
        if(connection.Host?.StartsWith("tcp://",StringComparison.OrdinalIgnoreCase)==true)
        {
            if(!Uri.TryCreate(connection.Host,UriKind.Absolute,out var hostUri)
                || hostUri.Scheme!="tcp" || !string.IsNullOrEmpty(hostUri.UserInfo)
                || hostUri.AbsolutePath!="/" || !string.IsNullOrEmpty(hostUri.Query))
                throw new InvalidOperationException("Database Host must be a hostname, not a malformed tcp:// address.");
            connection.Host=hostUri.Host;
            if(!hostUri.IsDefaultPort) connection.Port=hostUri.Port;
        }
        if(string.IsNullOrWhiteSpace(connection.Password) || connection.Password.Contains("YOUR-PASSWORD",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Store the actual database password in user-secrets or Database__Password; the supplied password is still a placeholder.");
        ValidateTarget(connection, config["Database:ProjectId"]);
        // Validate certificate AND hostname. Never silently downgrade TLS.
        connection.SslMode=SslMode.VerifyFull;
        var rootCertificate = config["Database:RootCertificate"];
        if (!string.IsNullOrWhiteSpace(rootCertificate))
        {
            if(!File.Exists(rootCertificate)) throw new InvalidOperationException("The configured database root certificate file does not exist. Restore the verified Supabase CA certificate; TLS verification remains required.");
            connection.RootCertificate=Path.GetFullPath(rootCertificate);
        }
        connection.IncludeErrorDetail=false;
        connection.PersistSecurityInfo=false;
        connection.Timeout=15;
        connection.CommandTimeout=30;
        connection.ApplicationName="GlamByMariya";
        // One budget per process, shared by EF and Data Protection. Session
        // pooler limit is 15: default 4 leaves room for a rolling-deploy overlap
        // (2 x 4) and other clients. Configuration may LOWER or adjust this up
        // to 6; reserve capacity across every process using this endpoint.
        var maximumPoolSize=config.GetValue("Database:MaxPoolSize",4);
        if(maximumPoolSize is <1 or >6)
            throw new InvalidOperationException("Database:MaxPoolSize must be between 1 and 6. Budget all app instances and other clients against the Supabase session limit.");
        connection.Pooling=true;
        connection.MinPoolSize=0;
        connection.MaxPoolSize=maximumPoolSize;
        connection.ConnectionIdleLifetime=60;
        connection.ConnectionPruningInterval=10;
        return connection.ConnectionString;
    }

    public static void ValidateTarget(NpgsqlConnectionStringBuilder connection, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) &&
            (connection.Host?.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase) == true ||
             connection.Host?.EndsWith(".pooler.supabase.com", StringComparison.OrdinalIgnoreCase) == true))
            throw new InvalidOperationException("Configure Database:ProjectId for the target Supabase project.");
        if (string.IsNullOrWhiteSpace(projectId)) return; // Isolated/local tests may omit a Supabase project.
        if (!System.Text.RegularExpressions.Regex.IsMatch(projectId, "^[a-z]{20}$"))
            throw new InvalidOperationException("Configure a valid Supabase Database:ProjectId.");
        var direct = string.Equals(connection.Host, $"db.{projectId}.supabase.co", StringComparison.OrdinalIgnoreCase);
        var pooler = connection.Host?.EndsWith(".pooler.supabase.com", StringComparison.OrdinalIgnoreCase) == true
            && connection.Username?.EndsWith("." + projectId, StringComparison.OrdinalIgnoreCase) == true;
        if (!direct && !pooler)
            throw new InvalidOperationException($"The configured database connection does not identify Supabase project {projectId}. Copy its Direct or Session pooler connection string from the Connect dialog. No connection was attempted.");
    }
}
