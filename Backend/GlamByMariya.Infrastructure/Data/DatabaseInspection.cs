using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Read-only metadata inspection. Never selects customer rows or changes a schema.
public static class DatabaseInspection
{
    public static async Task<string> InspectAsync(MakeupDbContext db, IConfiguration configuration, string contentRoot)
    {
        await using var connectionLease = await DatabaseConnectionLease.OpenAsync(db.Database);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var relations = await db.Database.SqlQueryRaw<RelationMetadata>("""
            SELECT n.nspname AS "Schema", c.relname AS "Name", c.relkind::text AS "Kind",
              c.relrowsecurity AS "RowLevelSecurity",
              has_table_privilege(c.oid,'SELECT') AS "CanRead",
              has_table_privilege(c.oid,'INSERT') AS "CanInsert",
              has_table_privilege(c.oid,'UPDATE') AS "CanUpdate",
              has_table_privilege(c.oid,'DELETE') AS "CanDelete"
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname IN ('public','makeup') AND c.relkind IN ('r','p','v','m','f')
            ORDER BY n.nspname,c.relname
            """).ToListAsync();
        var columns = await db.Database.SqlQueryRaw<ColumnMetadata>("""
            SELECT n.nspname AS "Schema", c.relname AS "Table", a.attname AS "Name",
              a.attnum::int AS "Ordinal", format_type(a.atttypid,a.atttypmod) AS "DataType",
              a.attnotnull AS "NotNull", a.attidentity::text AS "IdentityKind", a.attgenerated::text AS "GeneratedKind",
              pg_get_expr(d.adbin,d.adrelid) AS "DefaultExpression"
            FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            WHERE n.nspname IN ('public','makeup') AND c.relkind IN ('r','p','v','m','f')
              AND a.attnum>0 AND NOT a.attisdropped
            ORDER BY n.nspname,c.relname,a.attnum
            """).ToListAsync();
        var constraints = await db.Database.SqlQueryRaw<ConstraintMetadata>("""
            SELECT n.nspname AS "Schema", c.relname AS "Table", k.conname AS "Name",
              k.contype::text AS "Kind", pg_get_constraintdef(k.oid,true) AS "Definition"
            FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname IN ('public','makeup') ORDER BY n.nspname,c.relname,k.conname
            """).ToListAsync();
        var indexes = await db.Database.SqlQueryRaw<IndexMetadata>("""
            SELECT schemaname AS "Schema", tablename AS "Table", indexname AS "Name", indexdef AS "Definition"
            FROM pg_indexes WHERE schemaname IN ('public','makeup') ORDER BY schemaname,tablename,indexname
            """).ToListAsync();
        var policies = await db.Database.SqlQueryRaw<PolicyMetadata>("""
            SELECT schemaname AS "Schema", tablename AS "Table", policyname AS "Name", cmd AS "Command",
              permissive AS "Permissive", roles::text AS "Roles", qual AS "UsingExpression", with_check AS "CheckExpression"
            FROM pg_policies WHERE schemaname IN ('public','makeup') ORDER BY schemaname,tablename,policyname
            """).ToListAsync();
        var enumValues = await db.Database.SqlQueryRaw<EnumMetadata>("""
            SELECT n.nspname AS "Schema", t.typname AS "Name", e.enumlabel AS "Value"
            FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace JOIN pg_enum e ON e.enumtypid=t.oid
            WHERE n.nspname IN ('public','makeup') ORDER BY n.nspname,t.typname,e.enumsortorder
            """).ToListAsync();
        var expectations = db.Model.GetEntityTypes().Select(entity => new
        {
            Schema = entity.GetViewSchema() ?? entity.GetSchema() ?? "public",
            Name = entity.GetViewName() ?? entity.GetTableName()!,
            Columns = entity.GetProperties().Select(property => property.GetColumnName()).ToArray()
        }).ToList();
        var gaps = new List<string>();
        var configuredSchema = configuration["Database:Schema"] ?? "makeup";
        if (expectations.Any(expected => expected.Schema != configuredSchema))
            gaps.Add($"Requested schema is {configuredSchema}, but the existing adapter targets a different schema. Map existing tables before enabling application writes.");
        foreach (var expected in expectations)
        {
            if (!relations.Any(r => r.Schema == expected.Schema && r.Name == expected.Name))
            {
                gaps.Add($"Adapter expects {expected.Schema}.{expected.Name}; it is absent. Do not create a duplicate instead of mapping existing public data.");
                continue;
            }
            foreach (var column in expected.Columns)
                if (!columns.Any(c => c.Schema == expected.Schema && c.Table == expected.Name && c.Name == column))
                    gaps.Add($"Adapter expects missing column {expected.Schema}.{expected.Name}.{column}.");
        }
        await transaction.CommitAsync(); // Read-only; no table records were accessed.
        await transaction.DisposeAsync();
        await connectionLease.DisposeAsync(); // Release before report serialization / disk I/O.
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var report = new
        {
            InspectedAtUtc = DateTime.UtcNow,
            ProjectId = configuration["Database:ProjectId"],
            ConfiguredSchema = configuration["Database:Schema"] ?? "makeup",
            Host = connection.Host,
            Port = connection.Port,
            Database = connection.Database,
            ReadOnly = true,
            ContainsRecordData = false,
            Relations = relations,
            Columns = columns,
            Constraints = constraints,
            Indexes = indexes,
            Policies = policies,
            EnumValues = enumValues,
            CurrentAdapterExpectations = expectations,
            MappingGaps = gaps
        };
        var folder = Path.Combine(contentRoot, "App_Data", "DatabaseInspection");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{configuration["Database:ProjectId"] ?? "database"}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Read-only database inspection succeeded. Found {relations.Count(r => r.Schema == "public")} public relations and {gaps.Count} current adapter mapping gaps.");
        Console.WriteLine("Metadata report (no record rows or connection secret): " + path);
        Console.WriteLine("This verifies access only, not completed public-schema integration. No migration or data change was applied.");
        return path;
    }

    public static async Task VerifyApplicationAsync(MakeupDbContext db, IConfiguration configuration)
    {
        await using var connectionLease = await DatabaseConnectionLease.OpenAsync(db.Database);
        _ = await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync();
        var intendedSchema = configuration["Database:Schema"] ?? "makeup";
        var mappedSchemas = db.Model.GetEntityTypes().Select(entity => entity.GetViewSchema() ?? entity.GetSchema() ?? "public").Distinct().ToArray();
        if (mappedSchemas.Any(schema => schema != intendedSchema))
            throw new InvalidOperationException($"Database access succeeded, but the current adapter is not mapped to the requested {intendedSchema} schema. Run --database-inspect and review the existing tables before creating EF Core migrations or allowing application writes.");
        // This checks the current adapter only; it never creates missing objects.
        _ = await db.Set<SettingsRow>().AsNoTracking().SingleAsync(x => x.Id == 1);
    }
}

public sealed class RelationMetadata
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool RowLevelSecurity { get; set; }
    public bool CanRead { get; set; }
    public bool CanInsert { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }
}
public sealed class ColumnMetadata
{
    public string Schema { get; set; } = "";
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public int Ordinal { get; set; }
    public string DataType { get; set; } = "";
    public bool NotNull { get; set; }
    public string IdentityKind { get; set; } = "";
    public string GeneratedKind { get; set; } = "";
    public string? DefaultExpression { get; set; }
}
public sealed class ConstraintMetadata
{
    public string Schema { get; set; } = "";
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Definition { get; set; } = "";
}
public sealed class IndexMetadata
{
    public string Schema { get; set; } = "";
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public string Definition { get; set; } = "";
}
public sealed class PolicyMetadata
{
    public string Schema { get; set; } = "";
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Permissive { get; set; } = "";
    public string Roles { get; set; } = "";
    public string? UsingExpression { get; set; }
    public string? CheckExpression { get; set; }
}
public sealed class EnumMetadata
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}
