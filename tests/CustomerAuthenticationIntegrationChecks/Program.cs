using Npgsql;

if(args is ["--startup-diagnostics"])
{
    CustomerStartupDiagnosticChecks.Run();
    return;
}
if(args is ["--role-compatibility"])
{
    await RoleMembershipCompatibilityChecks.RunAsync();
    return;
}
if(args is ["--profile"])
{
    await CustomerIntegrationProfileChecks.RunAsync();
    return;
}

const string database="glam_v2_customer_auth_test";
const int port=55434;
var root=new DirectoryInfo(Directory.GetCurrentDirectory());
while (root!=null && !Directory.Exists(Path.Combine(root.FullName,"Frontend"))) root=root.Parent;
if(root==null) throw new InvalidOperationException("V2 workspace not found.");
var data=Path.GetFullPath(Path.Combine(root.FullName,"temp","v2-customer-auth-integration-20261116-4","data"));
var password=Environment.GetEnvironmentVariable("GLAM_V2_CUSTOMER_AUTH_TEST_PASSWORD");
if(args.Length!=1 || args[0]!="--exercise" || password is not {Length:64} ||
    !password.All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F')) throw new InvalidOperationException("Fresh isolated cluster and synthetic password required.");
var cs=new NpgsqlConnectionStringBuilder {Host="127.0.0.1",Port=port,Database="postgres",Username="postgres",
    Password=password,Pooling=false,SslMode=SslMode.Disable,Timeout=5,CommandTimeout=20};
await using var cluster=NpgsqlDataSource.Create(cs.ConnectionString);
await using(var c=await cluster.OpenConnectionAsync())
{
    await using var q=new NpgsqlCommand("""
        SELECT inet_server_addr()='127.0.0.1'::inet AND inet_server_port()=55434
          AND current_database()='postgres' AND current_user='postgres'
          AND current_setting('server_version_num')::integer BETWEEN 180000 AND 189999
          AND NOT EXISTS(SELECT 1 FROM pg_database WHERE datname IN
            ('glam_v2_customer_auth_test','glam_v2_auth_order_test','glam_v2_auth_repeat_test'))
          AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname IN
            ('anon','authenticated','service_role','glam_v2_backend','glam_v2_authz_owner','glam_v2_public_probe','glam_v2_inherited_probe','glam_v2_migration_probe')),
          current_setting('data_directory')
        """,c);
    await using(var reader=await q.ExecuteReaderAsync())
    {
        if(!await reader.ReadAsync() || !reader.GetBoolean(0) || !string.Equals(Path.GetFullPath(reader.GetString(1)),data,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing a non-fresh or unexpected PostgreSQL target.");
    }
    await using var create=new NpgsqlCommand($"""
        CREATE ROLE anon LOGIN NOINHERIT PASSWORD '{password}';
        CREATE ROLE authenticated LOGIN INHERIT PASSWORD '{password}';
        CREATE ROLE service_role LOGIN NOINHERIT BYPASSRLS PASSWORD '{password}';
        CREATE ROLE glam_v2_public_probe LOGIN NOINHERIT PASSWORD '{password}';
        CREATE ROLE glam_v2_inherited_probe NOLOGIN;
        CREATE ROLE glam_v2_migration_probe LOGIN INHERIT BYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{password}';
        CREATE ROLE glam_v2_authz_owner NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS;
        CREATE ROLE glam_v2_backend LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}';
        """,c);
    await create.ExecuteNonQueryAsync();
    await using var createDatabase=new NpgsqlCommand("CREATE DATABASE glam_v2_customer_auth_test TEMPLATE template0",c);
    await createDatabase.ExecuteNonQueryAsync();
}
cs.Database=database;
await using var admin=NpgsqlDataSource.Create(cs.ConnectionString);
await CustomerAuthMigrationChecks.PrepareAsync(root.FullName,cluster,admin,cs);
cs.Username="glam_v2_backend";
await using var backend=NpgsqlDataSource.Create(cs.ConnectionString);
await CustomerAuthMigrationChecks.PermissionsAsync(admin,cs);
await CustomerMvcChecks.RunAsync(admin,backend);
await CustomerAuthMigrationChecks.AfterMvcAsync(root.FullName,admin,backend);
