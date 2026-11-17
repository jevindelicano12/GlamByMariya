using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GlamByMariya.Infrastructure.Migrations.Public;

[DbContext(typeof(PublicMakeupDbContext))]
[Migration("20261016160000_ProfileAvatars")]
public sealed class ProfileAvatars : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE public.account_preferences ADD COLUMN avatar_url text;");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE public.account_preferences DROP COLUMN avatar_url;");
}
