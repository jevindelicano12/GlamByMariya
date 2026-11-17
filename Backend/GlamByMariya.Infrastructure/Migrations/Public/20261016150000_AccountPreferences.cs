using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GlamByMariya.Infrastructure.Migrations.Public;

[DbContext(typeof(PublicMakeupDbContext))]
[Migration("20261016150000_AccountPreferences")]
public sealed class AccountPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE public.account_preferences (
            user_id uuid PRIMARY KEY REFERENCES public.account_identities(id),
            verified_email text NOT NULL DEFAULT '',
            verified_at timestamptz,
            notifications_enabled boolean NOT NULL DEFAULT true
        );
        ALTER TABLE public.account_preferences ENABLE ROW LEVEL SECURITY;
        REVOKE ALL ON public.account_preferences FROM anon, authenticated;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE public.account_preferences;");
}
