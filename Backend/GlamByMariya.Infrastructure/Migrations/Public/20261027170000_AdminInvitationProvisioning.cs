using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GlamByMariya.Infrastructure.Migrations.Public;

[DbContext(typeof(PublicMakeupDbContext))]
[Migration("20261027170000_AdminInvitationProvisioning")]
public sealed class AdminInvitationProvisioning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Additive preparation only; identity tracking and roles/invitations are
        // ordered prerequisites. This migration does not activate provisioning.
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Sql", "AdminInvitationProvisioning.sql"));
        migrationBuilder.Sql(sql);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("AdminInvitationProvisioning cannot be reverted automatically; restore from a backup.");
}
