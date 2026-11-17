using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GlamByMariya.Infrastructure.Migrations.Public;

[DbContext(typeof(PublicMakeupDbContext))]
[Migration("20261017140000_DownPaymentPolicy")]
public sealed class DownPaymentPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Additive policy columns + snapshot-aware deposit validation. Existing rows keep
        // their historical 50% obligation through coalesced snapshots; payments are untouched.
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Sql", "DownPaymentPolicy.sql"));
        migrationBuilder.Sql(sql);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("DownPaymentPolicy cannot be reverted automatically; restore from a backup.");
}
