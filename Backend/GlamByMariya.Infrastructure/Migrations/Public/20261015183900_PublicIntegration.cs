using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GlamByMariya.Infrastructure.Migrations.Public
{
    /// <inheritdoc />
    public partial class PublicIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reviewed additive payload extends existing public tables without
            // dropping any table, row, primary key or foreign key.
            // Executed from the deployed SQL file so Table Editor verification
            // matches the reviewed Data/Sql/PublicIntegration.sql exactly.
            var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Sql", "PublicIntegration.sql"));
            migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Downgrades are blocked to protect existing Supabase records.
            // Restore from a backup instead of reverting this migration.
            throw new NotSupportedException("PublicIntegration cannot be reverted automatically. Restore from a backup.");
        }
    }
}
