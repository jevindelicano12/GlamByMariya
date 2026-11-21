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
    var raced = (long)(await Scalar($"SELECT count(*) FROM public.reservations WHERE start_at=(SELECT start_at FROM public.reservations WHERE reference LIKE 'GBM-%' ORDER BY id DESC LIMIT 1)"))!;
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
    var dueStore = App(); var dueAt = dueStore.FindBooking(paid.Id)?.PaymentDueAt;
    Check(dueAt is { } d && d > dueStore.Now && d <= dueStore.Now.AddMinutes(31), $"booking details know the unpaid booking's payment deadline (due {dueAt}, now {dueStore.Now})");
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

if (Run("--staff-actions"))
{
    // The owner (SuperAdmin) cancels or completes appointments in database mode, run as the app login.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    PublicStudioStore App() => NewStore(as_: appSource);
    var owner = App().CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    var customer = App().AddCustomerWithPasswordHash("Ana", "Cruz", "ana@example.test", hasher.HashPassword(new(), "AnaPass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var soft = App().Services.Single(x => x.Name == "Soft Glam");
    var n = 0;
    GlamByMariya.Core.Entities.Booking Confirmed(DateTime start)
    {
        var s = App(); n++;
        var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, s.Settings.TravelFee, "auto", s.Settings.DownPaymentPercent);
        var b = s.SubmitBooking(customer.Id, soft.Id, [], start, "24 Synthetic Street, Quezon City", "Quezon City", quote.RequiredDownPayment, "Xendit", $"XENDIT-STAFF-{n}", null,
            true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment);
        App().AttachInvoice(b.Id, $"inv_staff_{n}", $"https://checkout-staging.xendit.co/web/inv_staff_{n}");
        App().XenditPaid($"inv_staff_{n}", b.RequiredDownPayment, $"inv_staff_{n}:PAID");
        return b;
    }
    // Moves an appointment in time (tests only): the guard forbids this outside an approved reschedule.
    async Task Move(Guid booking, string startsIn, string endsIn) => await Exec($"""
        SET session_replication_role=replica;
        UPDATE public.reservations SET start_at=now()+interval '{startsIn}', end_at=now()+interval '{endsIn}', reserved_until=now()+interval '{endsIn}' WHERE app_id='{booking}';
        SET session_replication_role=origin;
        """);
    async Task<string> Status(Guid booking) => (string)(await Scalar($"SELECT status FROM public.reservations WHERE app_id='{booking}'"))!;
    string Error(Action action) { try { action(); return ""; } catch (InvalidOperationException e) { return e.Message; } }
    var day = DateTime.Today.AddDays(4);

    var soon = Confirmed(day.AddHours(10));
    await Move(soon.Id, "3 hours", "4 hours");
    var customerTry = Error(() => App().CancelBooking(soon.Id, customer.Id, true));
    Check(customerTry != "" && await Status(soon.Id) == "Confirmed", "a customer still cannot cancel within 24 hours");
    var noReason = Error(() => App().ManageBooking(soon.Id, "cancel", owner.Id, " ", 0, ""));
    Check(noReason.Contains("reason") && await Status(soon.Id) == "Confirmed", "the owner must give a cancellation reason");
    var cancelled = Error(() => App().ManageBooking(soon.Id, "cancel", owner.Id, "Artist is sick", 0, ""));
    Check(cancelled == "" && await Status(soon.Id) == "Cancelled", "the owner can cancel a confirmed appointment within 24 hours" + (cancelled == "" ? "" : ": " + cancelled));
    var closure = (string)(await Scalar($"SELECT cancellation_fee::int||'/'||verified_paid_snapshot::int FROM public.reservation_closures z JOIN public.reservations r ON r.id=z.reservation_id WHERE r.app_id='{soon.Id}'"))!;
    Check(closure == $"0/{(int)soon.RequiredDownPayment}", $"a business cancellation has no fee and the whole down payment is due back ({closure})");
    var shown = App().FindBooking(soon.Id);
    Check(shown?.StaffCancelledAt != null && shown.StaffCancelledBy == owner.Id && shown.StaffCancellationReason == "Artist is sick" && shown.RefundDue == soon.RequiredDownPayment,
        "booking details show the business cancellation, its reason and the refund due");

    var forged = Confirmed(day.AddHours(14));
    var forgedError = "";
    try { await Exec($"UPDATE public.reservations SET status='Cancelled', cancellation_confirmed_at=now(), staff_cancelled_by='{customer.Id}', staff_cancelled_at=now(), staff_cancellation_reason='x' WHERE app_id='{forged.Id}'"); }
    catch (PostgresException e) { forgedError = e.MessageText; }
    Check(forgedError.Contains("SuperAdmin") && await Status(forged.Id) == "Confirmed", "the database refuses a business cancellation not made by the active SuperAdmin");

    var future = Confirmed(day.AddDays(1).AddHours(10));
    var early = Error(() => App().ManageBooking(future.Id, "complete", owner.Id, "", 0, ""));
    Check(early != "" && await Status(future.Id) == "Confirmed", "an appointment that has not ended cannot be completed");

    var done = Confirmed(day.AddDays(1).AddHours(14));
    await Move(done.Id, "-3 hours", "-2 hours");
    var completed = Error(() => App().ManageBooking(done.Id, "complete", owner.Id, "", 0, ""));
    Check(completed == "" && await Status(done.Id) == "Completed", "the owner can complete an ended appointment" + (completed == "" ? "" : ": " + completed));
    var balance = (string)(await Scalar($"SELECT p.method||'/'||p.status||'/'||p.amount::int FROM public.payments p JOIN public.reservations r ON r.id=p.reservation_id WHERE r.app_id='{done.Id}' AND p.purpose='balance'"))!;
    Check(balance == $"Cash/Paid/{(int)(done.Total - done.RequiredDownPayment)}", $"completing records the remaining balance as received in cash ({balance})");
    var after = App().FindBooking(done.Id);
    Check(after?.CompletedAt != null && after.CompletedBy == owner.Id, "booking details show who completed the appointment and when");
    Console.WriteLine($"All {checks - before} staff action database checks passed.");
}

if (Run("--website-content"))
{
    // Website text drafts and publishing, run as the app login through the real repository.
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    var owner = NewStore(as_: appSource).CreateInitialSuperAdmin("Maria", "Santos", "maria@example.test", "OwnerPass123");
    var repo = new GlamByMariya.Infrastructure.Data.PostgresWebsiteContentRepository(appSource);
    var first = await repo.CreateDraft(owner.Id, default);
    Check(first.Revision == 1 && first.Status == "draft", "a new draft starts at revision 1");
    Check((await repo.CreateDraft(owner.Id, default)).Id == first.Id, "creating a draft again returns the existing draft");
    var text = new GlamByMariya.Core.BusinessRules.WebsiteTextInput("Glam at home", "Makeup that comes to you", "Meet Mariya", "Bio", "Home service", "Quezon City", "Call me",
        [new("Instagram", "https://instagram.com/glam")], [new("Do you travel?", "Yes, within Metro Manila.")]);
    var saved = await repo.SaveDraft(owner.Id, first.Id, first.Revision, text, default);
    var read = await repo.ReadDraft(default);
    Check(saved.Revision == 2 && read!.Text.HeroHeadline == "Glam at home" && read.Text.AboutHeading == "Meet Mariya"
        && read.Text.Links!.Single().Label == "Instagram" && read.Text.Faqs!.Single().Question == "Do you travel?", "a saved draft reads back its text, link and FAQ");
    var stale = "";
    try { await repo.SaveDraft(owner.Id, first.Id, 1, text, default); } catch (InvalidOperationException e) { stale = e.Message; }
    Check(stale != "", "saving with an old revision is refused");
    await repo.Publish(owner.Id, first.Id, saved.Revision, default);
    Check((await repo.ReadPublished(default))!.Text.AboutHeading == "Meet Mariya" && await repo.ReadDraft(default) == null, "publishing makes the draft the live version");
    var next = await repo.CreateDraft(owner.Id, default);
    Check(next.Id != first.Id && next.Text.AboutHeading == "Meet Mariya" && next.Text.Links!.Count == 1 && next.Text.Faqs!.Count == 1,
        "a new draft copies the published text, links and FAQs");
    var duplicate = "";
    try { await Exec("INSERT INTO public.website_content_versions(id,status) VALUES(gen_random_uuid(),'draft')"); } catch (PostgresException e) { duplicate = e.SqlState ?? ""; }
    Check(duplicate == "23505", "a second draft row is refused by the database");
    string Key() => "website-content/" + Guid.NewGuid().ToString("N") + ".jpg";
    var writer = (GlamByMariya.Infrastructure.Services.IWebsiteAssetWriter)repo;
    var assetA = await writer.AddAsset(owner.Id, Key(), "image/jpeg", 1234, default);
    var heroKey = Key();
    var assetHero = await writer.AddAsset(owner.Id, heroKey, "image/jpeg", 2000, default);
    var assetB = await writer.AddAsset(owner.Id, Key(), "image/jpeg", 3000, default);
    Check(assetA != Guid.Empty && assetA != assetHero, "adding an image record returns a new id");
    var badKey = "";
    try { await writer.AddAsset(owner.Id, "other/x.jpg", "image/jpeg", 10, default); } catch (PostgresException e) { badKey = e.SqlState ?? ""; }
    Check(badKey == "23514", "an image key outside website-content/ is refused by the database");
    var heroId = Guid.NewGuid();
    var imageList = new List<GlamByMariya.Infrastructure.Services.WebsiteImageReference> {
        new(heroId, assetHero, "hero", "Mariya at work", "", 0, true),
        new(Guid.NewGuid(), assetA, "gallery", "Bridal look", "Bride", 1, true),
        new(Guid.NewGuid(), assetB, "gallery", "Evening look", "", 2, true) };
    await repo.ReplaceDraftImages(owner.Id, next.Id, next.Revision, imageList, default);
    Check((await repo.ReadDraftImages(next.Id, default)).Count == 3, "a draft keeps its hero and two gallery images");
    var staleImages = "";
    try { await repo.ReplaceDraftImages(owner.Id, next.Id, next.Revision, imageList, default); } catch (InvalidOperationException e) { staleImages = e.Message; }
    Check(staleImages != "", "replacing images with an old revision is refused");
    Check((await repo.FindDraftImage(heroId, default))?.ObjectKey == heroKey, "a draft image can be found before publishing");
    await repo.Publish(owner.Id, next.Id, next.Revision + 1, default);
    Check((await repo.ReadPublishedImages(default)).Count == 3, "publishing makes the draft images live");
    Check((await repo.FindPublishedImage(heroId, default))?.ObjectKey == heroKey, "a published image is found by its id");
    Check(await repo.FindDraftImage(heroId, default) == null, "a published image is no longer a draft image");
    var nextDraft = await repo.CreateDraft(owner.Id, default);
    Check((await repo.ReadDraftImages(nextDraft.Id, default)).Count == 3, "a new draft copies the published images");
    var recorded = (long)(await Scalar("SELECT count(*) FROM public.schema_migrations"))!;
    Check(recorded == Directory.GetFiles(migrations, "*.sql").Length, "every migration file is recorded");
    Console.WriteLine($"All {checks - before} website content database checks passed.");
}

if (Run("--travel"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    var store = NewStore();
    var owner = store.CreateInitialSuperAdmin("Store", "Owner", "owner@example.test", "OwnerPass123");
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<GlamByMariya.Core.Entities.AppUser>();
    GlamByMariya.Core.Entities.AppUser Customer(string first) => store.AddCustomerWithPasswordHash(first, "Travel", first.ToLowerInvariant() + "@example.test",
        hasher.HashPassword(new(), first + "Pass123"), 30, "Female", "24 Synthetic Street, Quezon City");
    var ana = Customer("Ana"); var bea = Customer("Bea"); var cia = Customer("Cia"); var dee = Customer("Dee"); var eli = Customer("Eli");
    // The owner's example: service from 7:00, a one-hour preparation buffer.
    await Exec("UPDATE public.business_settings SET open_hour=7, close_hour=22, buffer_minutes=60, travel_speed_kmh=20, travel_road_factor=1.40, travel_margin_minutes=15, travel_unknown_minutes=60 WHERE id=1");
    var soft = NewStore().Services.Single(x => x.Name == "Soft Glam");
    var day = DateTime.Today.AddDays(5);
    const string travelMsg = "This time does not leave enough travel time between appointments. Please choose another time.";
    GlamByMariya.Core.Entities.Booking Book(Guid customer, DateTime start, string reference, double? lat, double? lng)
    {
        var s = NewStore();
        var quote = GlamByMariya.Core.BusinessRules.BookingPricing.Calculate(soft, [], 1, s.Settings.TravelFee, "auto", s.Settings.DownPaymentPercent);
        return s.SubmitBooking(customer, soft.Id, [], start, "24 Synthetic Street, Quezon City", "Quezon City", quote.RequiredDownPayment, "GCash", reference, null,
            true, true, 1, "auto", quote.FinalTotal, quote.RequiredDownPayment, lat, lng);
    }
    string Refusal(Action action) { try { action(); } catch (InvalidOperationException e) { return e.Message; } return ""; }
    async Task<int> SqlTravel(double a, double b, double c, double d) =>
        (int)(await Scalar(FormattableString.Invariant($"SELECT public.mvc_travel_minutes({a}::numeric,{b}::numeric,{c}::numeric,{d}::numeric)")))!;
    var settings = NewStore().Settings;

    // 1. App and database agree on travel minutes.
    var rng = new Random(13);
    var mismatches = 0;
    for (var i = 0; i < 300; i++)
    {
        double R(double lo, double hi) => Math.Round(lo + rng.NextDouble() * (hi - lo), 6);
        var (a, b, c, d) = (R(14.35, 14.85), R(120.85, 121.20), R(14.35, 14.85), R(120.85, 121.20));
        if (await SqlTravel(a, b, c, d) != GlamByMariya.Core.BusinessRules.TravelEstimate.Minutes(settings, a, b, c, d)) mismatches++;
    }
    Check(mismatches == 0, "app and database compute the same travel minutes for 300 random Metro Manila trips");
    Check(await SqlTravel(14.6760, 121.0437, 14.6760, 121.0437) == 0 && await SqlTravel(14.6760, 121.0437, 14.67605, 121.04375) == 0, "the same spot (under 100 m) needs no travel");
    Check((int)(await Scalar("SELECT public.mvc_travel_minutes(NULL,NULL,NULL,NULL)"))! == 0, "two bookings without pins keep the old rule (no travel added)");
    Check((int)(await Scalar("SELECT public.mvc_travel_minutes(14.6760,121.0437,NULL,NULL)"))! == 60, "one booking without a pin uses the 60-minute fallback");

    // Two spots about one hour apart with these settings: Quezon City and a point ~10.7 km south.
    var (qcLat, qcLng) = (14.676000, 121.043700);
    var (farLat, farLng) = (14.580000, 121.043700);
    var hour = await SqlTravel(qcLat, qcLng, farLat, farLng);
    Check(hour is >= 55 and <= 65, $"test spots are about an hour apart ({hour} min)");

    // 2. The owner's example: 7:00 booking, next customer one hour away.
    var first = Book(ana.Id, day.AddHours(7), "TRAVEL-1", qcLat, qcLng);
    var reservedUntil = first.Start.AddMinutes(soft.Minutes + 60);
    var earliest = reservedUntil.AddMinutes(hour);
    Check(Refusal(() => Book(bea.Id, earliest.AddMinutes(-5), "TRAVEL-2", farLat, farLng)) == travelMsg, "a far customer cannot start before session + buffer + travel");
    Check(Refusal(() => Book(bea.Id, reservedUntil, "TRAVEL-3", farLat, farLng)) == travelMsg, "right after the buffer is still too soon without travel time");
    var second = Book(bea.Id, earliest, "TRAVEL-4", farLat, farLng);
    Check(second.Start == earliest, $"the far customer can start exactly at {earliest:HH:mm} (end + buffer + travel)");

    // 3. A new booking before an existing one must leave travel time to reach it.
    var lateAnchor = Book(cia.Id, day.AddDays(1).AddHours(15), "TRAVEL-5", qcLat, qcLng);
    var latestEnd = lateAnchor.Start.AddMinutes(-hour - 60);
    Check(Refusal(() => Book(dee.Id, latestEnd.AddMinutes(-soft.Minutes + 5), "TRAVEL-6", farLat, farLng)) == travelMsg, "an earlier booking must leave travel time to the next one");
    Check(Book(dee.Id, latestEnd.AddMinutes(-soft.Minutes), "TRAVEL-7", farLat, farLng).Start == latestEnd.AddMinutes(-soft.Minutes), "an earlier booking that leaves exactly enough time is accepted");

    // 4. Same spot back-to-back needs no travel.
    var sameSpot = Book(eli.Id, second.Start.AddMinutes(soft.Minutes + 60), "TRAVEL-8", farLat + 0.0002, farLng);
    Check(sameSpot.Start == second.Start.AddMinutes(soft.Minutes + 60), "a neighbour at the same spot can book straight after the buffer");

    // 5. Old bookings without a pin use the fallback against a pinned one.
    await Exec("UPDATE public.business_settings SET open_hour=7 WHERE id=1");
    var legacyDay = day.AddDays(2);
    var legacy = Book(ana.Id, legacyDay.AddHours(9), "TRAVEL-9", null, null);
    var afterLegacy = legacy.Start.AddMinutes(soft.Minutes + 60);
    Check(Refusal(() => Book(bea.Id, afterLegacy.AddMinutes(30), "TRAVEL-10", qcLat, qcLng)) == travelMsg, "a pinned booking keeps the fallback travel time after an unpinned one");
    Check(Book(bea.Id, afterLegacy.AddMinutes(60), "TRAVEL-11", qcLat, qcLng).Start == afterLegacy.AddMinutes(60), "the fallback travel time is enough");

    // 6. Cancelled and expired bookings stop blocking travel time.
    var freeDay = day.AddDays(3);
    var gone = Book(cia.Id, freeDay.AddHours(9), "TRAVEL-12", qcLat, qcLng);
    var gap = gone.Start.AddMinutes(soft.Minutes + 60);
    Check(Refusal(() => Book(dee.Id, gap, "TRAVEL-13", farLat, farLng)) == travelMsg, "a pending booking blocks travel time");
    await Exec($"UPDATE public.reservations SET payment_expires_at=now()-interval '1 minute' WHERE app_id='{gone.Id}'");
    Check(Book(dee.Id, gap, "TRAVEL-14", farLat, farLng).Start == gap, "an expired unpaid booking no longer blocks travel time");

    // 7. Pins are validated and cannot be changed afterwards.
    var badPin = "";
    try { Book(eli.Id, day.AddDays(4).AddHours(9), "TRAVEL-15", 95, 121); } catch (Exception e) { badPin = e.Message; }
    Check(badPin != "", "an impossible latitude is refused by the database");
    var moved = "";
    try { await Exec($"UPDATE public.reservations SET service_lat=14.5 WHERE app_id='{second.Id}'"); } catch (PostgresException e) { moved = e.MessageText; }
    Check(moved.Contains("cannot be changed"), "a booking's pinned location cannot be changed");
    Check(GlamByMariya.Core.BusinessRules.TravelEstimate.ValidatePin(settings, null, 121) != null
        && GlamByMariya.Core.BusinessRules.TravelEstimate.ValidatePin(settings, double.NaN, 121) != null
        && GlamByMariya.Core.BusinessRules.TravelEstimate.ValidatePin(settings, 14.6, double.PositiveInfinity) != null
        && GlamByMariya.Core.BusinessRules.TravelEstimate.ValidatePin(settings, 10.3157, 123.8854) != null
        && GlamByMariya.Core.BusinessRules.TravelEstimate.ValidatePin(settings, qcLat, qcLng) == null,
        "missing, NaN, infinite and out-of-area (Cebu) pins are rejected; a Quezon City pin is accepted");

    // 8. Racing customers: base slots do not overlap, but travel does. Exactly one wins.
    var raceDay = day.AddDays(6);
    var x = raceDay.AddHours(9);
    var y = x.AddMinutes(soft.Minutes + 60);
    using var barrier = new Barrier(2);
    var racers = new (Guid Customer, DateTime Start, double Lat, double Lng, string Ref)[] { (ana.Id, x, qcLat, qcLng, "RACE-A"), (bea.Id, y, farLat, farLng, "RACE-B") };
    var results = await Task.WhenAll(racers.Select(r => Task.Run(() =>
    {
        barrier.SignalAndWait();
        try { Book(r.Customer, r.Start, r.Ref, r.Lat, r.Lng); return ""; } catch (InvalidOperationException e) { return e.Message; }
    })));
    var raceCount = (long)(await Scalar($"SELECT count(*) FROM public.reservations WHERE reference IN (SELECT reference FROM public.reservations WHERE start_at::date = '{raceDay:yyyy-MM-dd}'::date)"))!;
    Check(results.Count(r => r == "") == 1 && results.Count(r => r == travelMsg) == 1 && raceCount == 1, "two far-apart customers racing for back-to-back times: one wins");

    // 9. The app's calendar matches the database.
    var flowStore = NewStore();
    var flow = new GlamByMariya.Infrastructure.Services.BookingService(flowStore, flowStore, flowStore, flowStore, flowStore);
    var calendar = flow.GetAvailability(day, soft.Minutes, null, default, farLat, farLng);
    var slots = calendar.Days.Single(d => d.Date == day.ToString("yyyy-MM-dd")).Slots;
    bool Open(DateTime t) => slots.Single(s => s.Start == t.ToString("yyyy-MM-ddTHH:mm")).Available;
    Check(!Open(reservedUntil) && !Open(earliest.AddMinutes(-15)), "the calendar hides times without enough travel for a far customer");
    var columns = (long)(await Scalar("SELECT count(*) FROM information_schema.routines r JOIN information_schema.parameters p ON p.specific_name=r.specific_name WHERE r.routine_name='mvc_occupied_travel' AND p.parameter_mode='OUT' AND p.parameter_name IN ('service_lat','service_lng','lat','lng')"))!;
    Check(columns == 0, "busy-time results never include other customers' coordinates");
    Console.WriteLine($"All {checks - before} travel time database checks passed.");
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
