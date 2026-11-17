using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GlamByMariya.Infrastructure.Migrations.Public
{
    /// <inheritdoc />
    [DbContext(typeof(PublicMakeupDbContext))]
    [Migration("20261017090000_RolesAndInvitations")]
    public partial class RolesAndInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive only: third role in read projections, SuperAdmin-aware
            // guard function, and the admin_invitations table. No drops.
            // Executed from the deployed SQL file so Table Editor verification
            // matches the reviewed Data/Sql/RolesAndInvitations.sql exactly.
            var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Sql", "RolesAndInvitations.sql"));
            migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Downgrades are blocked to protect existing Supabase records.
            // Restore from a backup instead of reverting this migration.
            throw new NotSupportedException("RolesAndInvitations cannot be reverted automatically. Restore from a backup.");
        }
    }
}
