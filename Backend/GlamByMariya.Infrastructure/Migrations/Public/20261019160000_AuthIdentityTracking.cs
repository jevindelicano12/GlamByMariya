using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GlamByMariya.Infrastructure.Migrations.Public;

[DbContext(typeof(PublicMakeupDbContext))]
[Migration("20261019160000_AuthIdentityTracking")]
public sealed class AuthIdentityTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preparation only: nullable linkage, Unknown default, no account backfill.
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Sql", "AuthIdentityTracking.sql"));
        migrationBuilder.Sql(sql);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("AuthIdentityTracking cannot be reverted automatically; restore from a backup.");
}
