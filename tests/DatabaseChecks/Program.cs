// Phase 2 database checks. Run only through tools/test-db.sh, which starts a throwaway
// postgres:17 container on 127.0.0.1 with a random marker:
//   tools/test-db.sh tests/DatabaseChecks [--migrator] [--schema] [--access]
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

if (args.Length < 3 || !int.TryParse(args[0], out var port) || port is < 1024 or > 65535 ||
    !Guid.TryParseExact(args[1], "N", out var marker) || marker == Guid.Empty)
    throw new Exception("Run via tools/test-db.sh; a fresh marked throwaway PostgreSQL is required.");
var sections = args.Skip(3).ToHashSet();
bool Run(string section) => sections.Count == 0 || sections.Contains(section);

var builder = new NpgsqlDataSourceBuilder($"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;SSL Mode=Disable;GSS Encryption Mode=Disable;Pooling=false;Timeout=15;Command Timeout=120");
async Task Guard(NpgsqlConnection physical)
{
    // The random marker proves this is the container tools/test-db.sh just started.
    await using var command = new NpgsqlCommand("""
        SELECT current_database()='postgres' AND current_user IN ('postgres','glam_app')
          AND current_setting('glam.docker_test',true)='on'
          AND current_setting('glam.payment_review_marker',true)=@marker
        """, physical);
    command.Parameters.AddWithValue("marker", marker.ToString("N"));
    if ((bool?)await command.ExecuteScalarAsync() != true) throw new System.Security.SecurityException("Unsafe PostgreSQL physical connection.");
}
builder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), Guard);
await using var source = builder.Build();
// The app's own login (member of makeup_backend, no RLS bypass), as on Supabase.
var appBuilder = new NpgsqlDataSourceBuilder($"Host=127.0.0.1;Port={port};Username=glam_app;Database=postgres;SSL Mode=Disable;GSS Encryption Mode=Disable;Pooling=false;Timeout=15;Command Timeout=120");
appBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), Guard);
await using var appSource = appBuilder.Build();

var root = Directory.GetCurrentDirectory();
var migrations = Path.Combine(root, "database", "migrations");
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
async Task<object?> Scalar(string sql)
{
    await using var c = await source.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    return await cmd.ExecuteScalarAsync();
}
async Task Exec(string sql)
{
    await using var c = await source.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    await cmd.ExecuteNonQueryAsync();
}
async Task ResetPublic() => await Exec("DROP SCHEMA public CASCADE; CREATE SCHEMA public; GRANT ALL ON SCHEMA public TO public;");
async Task<string> Refused(Func<Task> action)
{
    try { await action(); } catch (InvalidOperationException e) { return e.Message; }
    return "";
}

var tempRoot = Path.GetFullPath(args[2]);
var http = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
var env = new TestEnv { ContentRootPath = tempRoot };
static void AppRetry(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
PublicStudioStore NewStore(Dictionary<string, string?>? settings = null, NpgsqlDataSource? as_ = null)
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build();
    var options = new DbContextOptionsBuilder<PublicMakeupDbContext>();
    // Same retry setting as the app (Program.cs), so transactions are tested the way they run.
    options.UseNpgsql(as_ ?? source, AppRetry);
    return new PublicStudioStore(new PublicMakeupDbContext(options.Options), http, env, config, new GlamByMariya.Infrastructure.Integrations.LocalAppFileStore(env));
}

if (Run("--migrator"))
{
    var temp = Path.Combine(Path.GetTempPath(), "glam-migrator-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        await ResetPublic();
        File.WriteAllText(Path.Combine(temp, "0001_a.sql"), "CREATE TABLE public.first_table(id int);");
        File.WriteAllText(Path.Combine(temp, "0002_b.sql"), "CREATE TABLE public.second_table(id int);");
        var applied = await SqlMigrator.ApplyAsync(source, temp);
        var versions = (long)(await Scalar("SELECT count(*) FROM public.schema_migrations WHERE version IN (1,2)"))!;
        Check(applied.SequenceEqual(["0001_a.sql", "0002_b.sql"]) && versions == 2, "applies 0001 and 0002 in order and records both");
        Check((await SqlMigrator.ApplyAsync(source, temp)).Count == 0, "second run applies nothing");
        Check(SqlMigrator.Checksum("a\r\nb") == SqlMigrator.Checksum("a\nb"), "CRLF and LF give the same checksum");

        File.WriteAllText(Path.Combine(temp, "0001_a.sql"), "CREATE TABLE public.first_table(id bigint);");
        Check((await Refused(() => SqlMigrator.ApplyAsync(source, temp))).Contains("0001_a.sql"), "an edited applied file is refused by name");
        File.WriteAllText(Path.Combine(temp, "0001_a.sql"), "CREATE TABLE public.first_table(id int);");

        File.WriteAllText(Path.Combine(temp, "0004_d.sql"), "SELECT 1;");
        Check((await Refused(() => SqlMigrator.ApplyAsync(source, temp))) != "", "a gap is refused");
        File.Delete(Path.Combine(temp, "0004_d.sql"));

        File.WriteAllText(Path.Combine(temp, "0003_c.sql"), "CREATE TABLE public.third_table(id int); SELECT broken syntax here;");
        var failed = false;
        try { await SqlMigrator.ApplyAsync(source, temp); } catch (PostgresException) { failed = true; }
        Check(failed && await Scalar("SELECT to_regclass('public.third_table')::text") is DBNull
              && (long)(await Scalar("SELECT count(*) FROM public.schema_migrations WHERE version=3"))! == 0,
            "a failing file leaves nothing behind");

        await ResetPublic();
        await SqlMigrator.ApplyAsync(source, migrations);
        Check(await Scalar("SELECT to_regclass('public.reservations')::text") is not DBNull, "real folder builds the base tables");
    }
    finally { Directory.Delete(temp, true); }
    Console.WriteLine($"All {checks} migrator checks passed.");
}

if (Run("--schema"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    // Every relation and function the app reads in database mode (mixed bookings and
    // website content excluded: they stay switched off there).
    string[] relations = ["account_identities", "account_preferences", "admin_invitations", "admin_users", "artist_availability", "audit_logs",
        "blocked_slots", "business_settings", "catalog_categories", "catalog_images", "customers", "data_protection_keys", "email_codes",
        "email_code_cooldowns", "makeup_addons", "makeup_artists", "media_assets", "notifications", "payment_adjustments", "payment_methods",
        "payment_revisions", "payments", "refunds", "reschedule_requests", "reservation_addons", "reservation_closures", "reservation_history",
        "reservations", "service_areas", "services", "mvc_blocks", "mvc_booking_addons", "mvc_booking_summary", "mvc_catalog_images",
        "mvc_closures", "mvc_payments", "mvc_reschedules", "mvc_roles", "mvc_services", "mvc_users", "payment_webhook_events"];
    var missing = new List<string>();
    foreach (var name in relations)
        if (await Scalar($"SELECT to_regclass('public.{name}')::text") is DBNull) missing.Add(name);
    foreach (var name in new[] { "mvc_assert_role", "mvc_check_slot" })
        if ((long)(await Scalar($"SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND p.proname='{name}'"))! == 0) missing.Add(name + "()");
    Check(missing.Count == 0, "every object the app reads exists" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
    var people = (long)(await Scalar("SELECT (SELECT count(*) FROM public.customers)+(SELECT count(*) FROM public.admin_users)+(SELECT count(*) FROM public.makeup_artists)+(SELECT count(*) FROM public.reservations)"))!;
    Check(people == 0, "seed has no people and no bookings");
    var catalog = (string)(await Scalar("""
        SELECT (SELECT count(*) FROM public.services WHERE is_active AND NOT is_archived)||','||(SELECT count(*) FROM public.makeup_addons)||','||
               (SELECT count(*) FROM public.service_areas WHERE is_active)||','||(SELECT count(*) FROM public.payment_methods)||','||
               (SELECT count(*) FROM public.business_settings WHERE id=1)
        """))!;
    Check(catalog == "3,3,6,5,1", $"seed has the catalog (services,add-ons,areas,methods,settings = {catalog})");
    var store = NewStore();
    Check(store.Services.Count(x => x.Active && !x.Archived) == 3 && store.AddOns.Count == 3 && store.Settings.OpenHour == 8 && store.Settings.BufferMinutes == 45,
        "the app reads the catalog and settings through its views");
    Console.WriteLine($"All {checks - before} schema checks passed.");
}

if (Run("--rules"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    var store = NewStore();
    var owner = store.CreateInitialSuperAdmin("Store", "Owner", "owner@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var ana = store.AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var bea = store.AddCustomerWithPasswordHash("Bea", "Reyes", "bea@example.test", hasher.HashPassword(new(), "BeaPass123"), 30, "Female", "25 Synthetic Street, Quezon City");
    var soft = store.Services.Single(x => x.Name == "Soft Glam");
    var day = DateTime.Today.AddDays(3);
    const string overlap = "This slot overlaps a reserved appointment. Please choose another time.";
    GlamByMariya.Core.Entities.Booking Book(PublicStudioStore s, Guid customer, DateTime start, string reference)
    {
        var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, s.Settings.TravelFee, "auto", s.Settings.DownPaymentPercent);
        return s.SubmitBooking(customer, soft.Id, [], start, "24 Synthetic Street, Quezon City", "Quezon City", quote.RequiredDownPayment, "GCash", reference, null,
            true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment);
    }
    async Task<string> Status(Guid booking) => (string)(await Scalar($"SELECT status FROM public.reservations WHERE app_id='{booking}'"))!;
    async Task Confirm(Guid booking) => await Exec($"""
        BEGIN; SELECT set_config('makeup.actor_id','{owner.Id}',true);
        UPDATE public.payments SET status='Paid', verification_status='Verified', verified_by='{owner.Id}', verified_at=now()
         WHERE reservation_id=(SELECT id FROM public.reservations WHERE app_id='{booking}');
        UPDATE public.reservations SET status='Confirmed' WHERE app_id='{booking}'; COMMIT;
        """);
    // Test setup only: move a booking in time without the reschedule workflow.
    async Task MoveStart(Guid booking, string fromNow) => await Exec($"""
        SET session_replication_role = replica;
        UPDATE public.reservations SET start_at=now()+interval '{fromNow}', end_at=now()+interval '{fromNow}'+(end_at-start_at),
          reserved_until=now()+interval '{fromNow}'+(reserved_until-start_at) WHERE app_id='{booking}';
        SET session_replication_role = origin;
        """);
    string Refusal(Action action) { try { action(); } catch (InvalidOperationException e) { return e.Message; } return ""; }

    var first = Book(store, ana.Id, day.AddHours(10), "RULES-001");
    Check(await Status(first.Id) == "Pending Payment", "a new booking waits for payment");
    Check(Refusal(() => Book(NewStore(), bea.Id, day.AddHours(10), "RULES-002")) == overlap, "pending payment holds the slot");

    await Exec($"UPDATE public.reservations SET payment_expires_at=now()-interval '1 minute' WHERE app_id='{first.Id}'");
    var second = Book(NewStore(), bea.Id, day.AddHours(10), "RULES-003");
    Check(await Status(first.Id) == "Expired" && await Status(second.Id) == "Pending Payment", "an overdue unpaid booking frees its slot at once");

    var racers = new[] { ana.Id, bea.Id };
    using var barrier = new Barrier(2);
    var results = await Task.WhenAll(racers.Select((customer, i) => Task.Run(() =>
    {
        var s = NewStore(); barrier.SignalAndWait();
        try { Book(s, customer, day.AddHours(14), $"RACE-00{i}"); return ""; } catch (InvalidOperationException e) { return e.Message; }
    })));
    var raced = (long)(await Scalar($"SELECT count(*) FROM public.reservations WHERE start_at=(SELECT start_at FROM public.reservations WHERE reference LIKE 'LM-%' ORDER BY id DESC LIMIT 1)"))!;
    Check(results.Count(r => r == "") == 1 && results.Count(r => r == overlap) == 1 && raced == 1, "two customers booking at the same moment: one wins");

    NewStore().CancelBooking(second.Id, bea.Id, true);
    var pendingFee = await Scalar($"SELECT cancellation_fee FROM public.reservation_closures WHERE reservation_id=(SELECT id FROM public.reservations WHERE app_id='{second.Id}')");
    Check(await Status(second.Id) == "Cancelled" && (pendingFee is DBNull || (decimal)pendingFee == 0), "customer cancels unpaid booking free");

    var early = Book(NewStore(), ana.Id, day.AddHours(8), "RULES-004");
    await Confirm(early.Id);
    await MoveStart(early.Id, "24 hours 5 minutes");
    NewStore().CancelBooking(early.Id, ana.Id, true);
    var fee = (decimal)(await Scalar($"SELECT cancellation_fee FROM public.reservation_closures WHERE reservation_id=(SELECT id FROM public.reservations WHERE app_id='{early.Id}')"))!;
    var late = Book(NewStore(), bea.Id, day.AddHours(16), "RULES-005");
    await Confirm(late.Id);
    await MoveStart(late.Id, "23 hours 59 minutes");
    var dbRefused = "";
    try { await Exec($"UPDATE public.reservations SET status='Cancelled', cancellation_confirmed_at=now() WHERE app_id='{late.Id}'"); }
    catch (PostgresException e) { dbRefused = e.MessageText; }
    Check(fee == 200 && dbRefused.Contains("24 hours"), "confirmed booking 24h away cancels with 200, 23h59 is refused");

    var overdue = Book(NewStore(), ana.Id, day.AddDays(1).AddHours(10), "RULES-006");
    var fresh = Book(NewStore(), bea.Id, day.AddDays(1).AddHours(14), "RULES-007");
    await Exec($"UPDATE public.reservations SET payment_expires_at=now()-interval '1 minute' WHERE app_id='{overdue.Id}'");
    Check(NewStore().ExpireOverduePayments() == 1 && await Status(overdue.Id) == "Expired" && await Status(fresh.Id) == "Pending Payment", "expiry job marks only overdue bookings");

    var invites = new GlamByMariya.Infrastructure.Services.PostgresInvitationService(new PublicMakeupDbContext(new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(source, AppRetry).Options), store, TimeProvider.System);
    Check(invites.InviteWithToken("new.admin@example.test", owner.Id).Invite.Email == "new.admin@example.test", "invitation by the owner is stored");
    Console.WriteLine($"All {checks - before} booking rule checks passed.");
}

if (Run("--owner"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    var store = NewStore();
    store.CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var artists = (string)(await Scalar("SELECT count(*)||':'||coalesce(string_agg(full_name, ','),'') FROM public.makeup_artists WHERE is_active"))!;
    Check(artists == "1:Maria Santos", $"owner setup creates the one artist with the owner's name ({artists})");
    var calendarStore = NewStore();
    var calendar = new GlamByMariya.Infrastructure.Services.BookingService(calendarStore, calendarStore, calendarStore, calendarStore, calendarStore).GetAvailability(DateTime.Today.AddMonths(1), 60);
    Check(calendar.Days.Any(d => d.Status == "available"), "booking calendar works without Database:ArtistId");
    var again = "";
    try { NewStore().CreateInitialSuperAdmin("Other", "Owner", "other.owner@example.test", "OwnerPass123"); } catch (InvalidOperationException e) { again = e.Message; }
    var after = (long)(await Scalar("SELECT count(*) FROM public.makeup_artists"))!;
    Check(again != "" && after == 1, "second owner setup is refused and adds no artist");
    Console.WriteLine($"All {checks - before} owner checks passed.");
}

if (Run("--access"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    var open = (await Scalar("""
        SELECT string_agg(c.relname, ', ') FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relkind IN ('r','v','m','p')
          AND (has_table_privilege('anon', c.oid, 'SELECT,INSERT,UPDATE,DELETE') OR has_table_privilege('authenticated', c.oid, 'SELECT,INSERT,UPDATE,DELETE'))
        """)) as string ?? "";
    Check(open == "", "anon and authenticated cannot read or write any public table" + (open == "" ? "" : ": " + open));
    var functions = (await Scalar("""
        SELECT string_agg(p.proname, ', ') FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname='public' AND (has_function_privilege('anon', p.oid, 'EXECUTE') OR has_function_privilege('authenticated', p.oid, 'EXECUTE'))
        """)) as string ?? "";
    Check(functions == "", "anon and authenticated cannot run public functions" + (functions == "" ? "" : ": " + functions));
    var recorded = (long)(await Scalar("SELECT count(*) FROM public.schema_migrations"))!;
    var cron = (long)(await Scalar("SELECT count(*) FROM pg_extension WHERE extname='pg_cron'"))!;
    Check(recorded == Directory.GetFiles(migrations, "*.sql").Length && cron == 0, "all migrations apply without pg_cron (local) and are recorded");
    Console.WriteLine($"All {checks - before} access checks passed.");
}

if (Run("--app-role"))
{
    // Everything the running app does, done as glam_app instead of the superuser.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    PublicStudioStore App() => NewStore(as_: appSource);
    var owner = App().CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    Check(owner.Role == "SuperAdmin", "the owner setup runs with the app login");
    async Task<string> AsApp(string sql)
    {
        try { await using var c = await appSource.OpenConnectionAsync(); await using var cmd = new NpgsqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); return ""; }
        catch (PostgresException e) { return e.MessageText; }
    }
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var customer = App().AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var app = App();
    var soft = app.Services.Single(x => x.Name == "Soft Glam");
    var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, app.Settings.TravelFee, "auto", app.Settings.DownPaymentPercent);
    var booking = app.SubmitBooking(customer.Id, soft.Id, [], DateTime.Today.AddDays(3).AddHours(10), "24 Synthetic Street, Quezon City", "Quezon City",
        quote.RequiredDownPayment, "GCash", "APP-ROLE-1", null, true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment);
    Check(App().FindBooking(booking.Id) != null, "the app login can register a customer and book");
    Check(App().ExpireOverduePayments() == 0, "the app login can run the expiry function");
    App().CancelBooking(booking.Id, customer.Id, true);
    Check((string)(await Scalar($"SELECT status FROM public.reservations WHERE app_id='{booking.Id}'"))! == "Cancelled", "the app login can cancel a booking");
    var invites = new GlamByMariya.Infrastructure.Services.PostgresInvitationService(new PublicMakeupDbContext(new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(appSource, AppRetry).Options), App(), TimeProvider.System);
    var inviteError = "";
    try { invites.InviteWithToken("new.admin@example.test", owner.Id); } catch (Exception e) { inviteError = e.GetBaseException().Message; }
    Check(inviteError == "", "the app login can store an admin invitation" + (inviteError == "" ? "" : ": " + inviteError));
    var prefs = await AsApp($"INSERT INTO public.account_preferences(user_id, verified_email) VALUES('{customer.Id}', 'ana@example.test'); SELECT * FROM public.account_preferences;");
    Check(prefs == "", "the app login can save account preferences" + (prefs == "" ? "" : ": " + prefs));
    var keys = await AsApp("INSERT INTO public.data_protection_keys(\"FriendlyName\", \"Xml\") VALUES('key-1', '<key/>'); SELECT * FROM public.data_protection_keys;");
    Check(keys == "", "the app login can store Data Protection keys" + (keys == "" ? "" : ": " + keys));
    var codes = await AsApp("SELECT count(*) FROM public.email_codes; SELECT count(*) FROM public.email_code_cooldowns;");
    Check(codes == "", "the app login can read email codes" + (codes == "" ? "" : ": " + codes));
    await Exec("CREATE FUNCTION public.zz_added_later() RETURNS int LANGUAGE sql AS 'SELECT 1'");
    var later = (bool)(await Scalar("SELECT has_function_privilege('anon', 'public.zz_added_later()', 'EXECUTE') OR has_function_privilege('authenticated', 'public.zz_added_later()', 'EXECUTE')"))!;
    Check(!later, "a function added by a later migration is not callable by anon or authenticated");
    Console.WriteLine($"All {checks - before} app role checks passed.");
}

if (Run("--xendit"))
{
    // Xendit deposits, run as the app login: invoice attached, webhook paid/duplicate/late/expired.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    PublicStudioStore App() => NewStore(as_: appSource);
    App().CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var customer = App().AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var soft = App().Services.Single(x => x.Name == "Soft Glam");
    GlamByMariya.Core.Entities.Booking BookXendit(DateTime start, string reference)
    {
        var s = App();
        var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, s.Settings.TravelFee, "auto", s.Settings.DownPaymentPercent);
        return s.SubmitBooking(customer.Id, soft.Id, [], start, "24 Synthetic Street, Quezon City", "Quezon City", quote.RequiredDownPayment, "Xendit", reference, null,
            true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment);
    }
    async Task<string> Status(Guid booking) => (string)(await Scalar($"SELECT status FROM public.reservations WHERE app_id='{booking}'"))!;
    var day = DateTime.Today.AddDays(4);
    var paid = BookXendit(day.AddHours(10), "XENDIT-TEST-1");
    App().AttachInvoice(paid.Id, "inv_test_1", "https://checkout-staging.xendit.co/web/inv_test_1");
    var url = (string)(await Scalar($"SELECT checkout_url FROM public.payments WHERE provider_invoice_id='inv_test_1'"))!;
    Check(await Status(paid.Id) == "Pending Payment" && url.EndsWith("inv_test_1"), "a Xendit booking waits for payment with its invoice attached");
    Check(App().PendingCheckoutUrl(paid.Id) == url, "the app can show the unpaid booking's Xendit payment link again");
    var wrong = "";
    try { App().XenditPaid("inv_test_1", paid.RequiredDownPayment - 1, "inv_test_1:PAID"); } catch (InvalidOperationException e) { wrong = e.Message; }
    Check(wrong.Contains("does not match") && await Status(paid.Id) == "Pending Payment", "a wrong paid amount does not confirm the booking");
    Check(App().XenditPaid("inv_test_1", paid.RequiredDownPayment, "inv_test_1:PAID") == "confirmed" && await Status(paid.Id) == "Confirmed",
        "the paid webhook confirms the booking");
    Check(App().PendingCheckoutUrl(paid.Id) == null, "a paid booking no longer offers the payment link");
    Check((string)(await Scalar($"SELECT status||'/'||verification_status FROM public.payments WHERE provider_invoice_id='inv_test_1'"))! == "Paid/Verified",
        "the deposit is recorded as paid and verified");
    Check(App().XenditPaid("inv_test_1", paid.RequiredDownPayment, "inv_test_1:PAID") == "duplicate", "the same webhook event twice is ignored");
    Check(App().XenditPaid("inv_test_1", paid.RequiredDownPayment, "inv_test_1:SETTLED") == "already", "a later SETTLED event changes nothing");
    var late = BookXendit(day.AddHours(14), "XENDIT-TEST-2");
    App().AttachInvoice(late.Id, "inv_test_2", "https://checkout-staging.xendit.co/web/inv_test_2");
    await Exec($"UPDATE public.reservations SET payment_expires_at=now()-interval '1 minute' WHERE app_id='{late.Id}'");
    Check(App().XenditExpired("inv_test_2", "inv_test_2:EXPIRED") == "noted" && await Status(late.Id) == "Expired", "the expired webhook releases the unpaid booking");
    Check(App().PendingCheckoutUrl(late.Id) == null, "an expired booking no longer offers the payment link");
    Check(App().XenditPaid("inv_test_2", late.RequiredDownPayment, "inv_test_2:PAID") == "late" && await Status(late.Id) == "Expired",
        "a payment after expiry is recorded for refund and does not confirm");
    var unknown = "";
    try { App().XenditPaid("inv_missing", 100, "inv_missing:PAID"); } catch (InvalidOperationException e) { unknown = e.Message; }
    Check(unknown.Contains("Unknown Xendit invoice"), "an unknown invoice is refused");
    Console.WriteLine($"All {checks - before} Xendit database checks passed.");
}

sealed class TestEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "DatabaseChecks";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
