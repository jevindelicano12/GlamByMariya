using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GlamByMariya.Infrastructure.Migrations.Public;

[DbContext(typeof(PublicMakeupDbContext))]
[Migration("20261018100000_TransportationFeePolicy")]
public sealed class TransportationFeePolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Reuses reservations.travel_reminder_acknowledged_at. No booking, payment,
        // customer, or financial data is updated.
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Sql", "TransportationFeePolicy.sql"));
        migrationBuilder.Sql(sql);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("TransportationFeePolicy cannot be reverted automatically; restore from a backup.");
}
