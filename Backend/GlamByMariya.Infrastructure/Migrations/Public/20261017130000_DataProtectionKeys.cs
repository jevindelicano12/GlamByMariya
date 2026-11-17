using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GlamByMariya.Infrastructure.Migrations.Public;

[DbContext(typeof(PublicMakeupDbContext))]
[Migration("20261017130000_DataProtectionKeys")]
public sealed class DataProtectionKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Additive, dedicated table for encrypted key-ring XML only.
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Sql", "DataProtectionKeys.sql"));
        migrationBuilder.Sql(sql);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("DataProtectionKeys cannot be automatically removed; it contains authentication key material.");
}
