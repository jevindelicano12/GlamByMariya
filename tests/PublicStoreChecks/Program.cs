using System.Security.Claims;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Npgsql;

if (args.Length is < 3 or > 4 || (args.Length == 4 && args[3] != "--mvc-booking") || !int.TryParse(args[0], out var port) || port is < 1024 or > 65535 ||
    !Guid.TryParseExact(args[1], "N", out var marker) || marker == Guid.Empty)
    throw new Exception("Run via isolated runner; a fresh marked loopback cluster is required.");
var cs = $"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;SSL Mode=Disable;GSS Encryption Mode=Disable;Pooling=false;Timeout=15;Command Timeout=120";
var builder = new NpgsqlDataSourceBuilder(cs);
async Task Guard(NpgsqlConnection physical)
{
    await using var command = new NpgsqlCommand("""
        SELECT current_database()='postgres' AND current_user='postgres'
          AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet
          AND inet_server_port()=@port AND current_setting('glam.payment_review_marker',true)=@marker
        """, physical);
    command.Parameters.AddWithValue("port", port);
    command.Parameters.AddWithValue("marker", marker.ToString("N"));
    if ((bool?)await command.ExecuteScalarAsync() != true) throw new System.Security.SecurityException("Unsafe PostgreSQL physical connection.");
}
builder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), Guard);
await using var source = builder.Build();
await using (var guard = await source.OpenConnectionAsync())
{
    using var command = new NpgsqlCommand("""
        SELECT to_regnamespace('makeup') IS NULL
          AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public')
          AND NOT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public')
        """, guard);
    if (command.ExecuteScalar() is not true) throw new InvalidOperationException("Unsafe or nonempty PostgreSQL test target.");
}
var root = Directory.GetCurrentDirectory();
var temp = Path.GetFullPath(args[2]);
var owner = Path.Combine(temp, "owner-marker");
if (!Directory.Exists(temp) || (File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0 ||
    !File.Exists(owner) || (File.GetAttributes(owner) & FileAttributes.ReparsePoint) != 0 ||
    File.ReadAllText(owner) != marker.ToString("N") || Directory.GetFileSystemEntries(temp).Length != 1)
    throw new InvalidOperationException("Unsafe or nonempty synthetic artifact directory.");
var env = new TestEnv { ContentRootPath = temp };
var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
var configValues = new Dictionary<string, string?>();
var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
int checks = 0;
void Check(bool c, string m) { if (!c) throw new Exception("FAIL: " + m); checks++; Console.WriteLine("PASS: " + m); }
void Reject(Action a, string m) { try { a(); } catch (InvalidOperationException) { Check(true, m); return; } throw new Exception("Expected rejection: " + m); }
void Login(AppUser u) => http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
    new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Role, u.Role), new Claim(ClaimTypes.Email, u.Email) }, "test"));
var proofBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6cWQAAAAASUVORK5CYII=");
string SyntheticProof(byte marker)
{
    var directory = Path.Combine(temp, "App_Data", "Proofs");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, Guid.NewGuid() + ".png");
    File.WriteAllBytes(path, [.. proofBytes, marker]);
    return path;
}
try
{
    await using (var setup = await source.OpenConnectionAsync())
    {
        async Task RunFile(string path)
        {
            var sql = await File.ReadAllTextAsync(path);
            // Split baseline only; migration runs whole (has dollar bodies).
            if (path.EndsWith("BaselinePublic.sql"))
            {
                foreach (var chunk in sql.Split(new[] { ";\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var s = string.Join('\n', chunk.Split('\n').Where(l => !l.TrimStart().StartsWith("--"))).Trim();
                    if (s.Length == 0) continue;
                    await using var cmd = new NpgsqlCommand(s + ";", setup) { CommandTimeout = 120 };
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            else
            {
                await using var cmd = new NpgsqlCommand(sql, setup) { CommandTimeout = 120 };
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await RunFile(Path.Combine(root, "tests", "PublicChecks", "BaselinePublic.sql"));
        await RunFile(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "PublicIntegration.sql"));
        var candidate = await File.ReadAllTextAsync(Path.Combine(root, "docs", "persistent-mixed-foundation-review-only.sql"));
        var blocked = false;
        try
        {
            await using var command = new NpgsqlCommand(candidate, setup);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException &&
                                          e.MessageText == "Persistent mixed foundation is review-only and incomplete")
        { blocked = true; }
        finally
        {
            await using var rollback = new NpgsqlCommand("ROLLBACK", setup);
            await rollback.ExecuteNonQueryAsync();
        }
        Check(blocked, "Incomplete mixed foundation aborts on the disposable PostgreSQL target");
        await using var verify = new NpgsqlCommand("""
            SELECT to_regclass('public.reservation_mixed_people') IS NULL
              AND to_regclass('public.reservation_mixed_person_addons') IS NULL
              AND (SELECT count(*) FROM public.reservations WHERE reference='LEGACY-001')=1
            """, setup);
        Check((bool?)await verify.ExecuteScalarAsync() == true, "Rejected mixed foundation leaves no mixed tables and preserves the fixture reservation");
        await MixedSnapshotStructureChecks.RunAsync(setup, candidate, Check, http, env, config);
        var inventory = await File.ReadAllTextAsync(Path.Combine(root, "docs", "public-standard-booking-read-only-inventory.sql"));
        await using (var inventoryCommand = new NpgsqlCommand(inventory, setup))
        await using (var inventoryRows = await inventoryCommand.ExecuteReaderAsync())
        {
            var resultSets = 0;
            do { resultSets++; while (await inventoryRows.ReadAsync()) { } } while (await inventoryRows.NextResultAsync());
            Check(resultSets >= 10, "read-only booking inventory executes against the disposable schema");
        }
        var standard = await File.ReadAllTextAsync(Path.Combine(root, "docs", "public-standard-booking-confirmed-rules-review-only.sql"));
        var standardBlocked = false;
        try
        {
            await using var guarded = new NpgsqlCommand(standard, setup);
            await guarded.ExecuteNonQueryAsync();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException &&
                                          e.MessageText == "Standard booking rules require separate migration review")
        { standardBlocked = true; }
        finally { await using var rollback = new NpgsqlCommand("ROLLBACK", setup); await rollback.ExecuteNonQueryAsync(); }
        Check(standardBlocked, "Standard booking candidate aborts before changes");
        const string begin = "-- BEGIN ISOLATED STANDARD RULES";
        const string end = "-- END ISOLATED STANDARD RULES";
        var first = standard.IndexOf(begin, StringComparison.Ordinal);
        var last = standard.IndexOf(end, StringComparison.Ordinal);
        if (first < 0 || last <= first || standard.IndexOf(begin, first + begin.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Invalid isolated standard-rule markers.");
        await using var isolated = new NpgsqlCommand(standard[(first + begin.Length)..last], setup);
        await isolated.ExecuteNonQueryAsync();
        await using (var roleTx = await setup.BeginTransactionAsync())
        {
            await using var roleCheck = new NpgsqlCommand("""
                SET LOCAL ROLE makeup_backend;
                SELECT count(*) FROM public.mvc_occupied_intervals(1,now(),now()+interval '1 day',NULL)
                """, setup, roleTx);
            _ = (long)(await roleCheck.ExecuteScalarAsync())!;
            Check(true, "restricted backend role can invoke the interval-only availability interface");
            await using (var hideOwner = new NpgsqlCommand("""
                RESET ROLE;
                CREATE ROLE occupancy_filtered_owner NOLOGIN;
                GRANT USAGE ON SCHEMA public TO occupancy_filtered_owner;
                GRANT SELECT ON public.reservations,public.blocked_slots TO occupancy_filtered_owner;
                ALTER FUNCTION public.mvc_occupied_intervals(bigint,timestamptz,timestamptz,uuid) OWNER TO occupancy_filtered_owner;
                SET LOCAL ROLE makeup_backend
                """, setup, roleTx))
                await hideOwner.ExecuteNonQueryAsync();
            var filteredOwnerDenied = false;
            try { await roleCheck.ExecuteScalarAsync(); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException) { filteredOwnerDenied = true; }
            await roleTx.RollbackAsync();
            Check(filteredOwnerDenied, "availability fails closed if its function owner is subject to RLS");
        }
        await using var privacyCheck = new NpgsqlCommand("""
            SELECT pg_get_function_result('public.mvc_occupied_intervals(bigint,timestamptz,timestamptz,uuid)'::regprocedure)
                = 'TABLE(occupied_from timestamp with time zone, occupied_until timestamp with time zone)'
               AND NOT EXISTS (
                 SELECT 1 FROM pg_proc p, LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
                 WHERE p.oid='public.mvc_occupied_intervals(bigint,timestamptz,timestamptz,uuid)'::regprocedure
                   AND a.grantee=0 AND a.privilege_type='EXECUTE')
            """, setup);
        Check((bool?)await privacyCheck.ExecuteScalarAsync() == true, "availability interface exposes only interval columns and denies PUBLIC execution");
    }
    var commands = new CountingCommands();
    var options = new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(source).AddInterceptors(commands).Options;
    using var db = new PublicMakeupDbContext(options);
    // Choose Angela Cruz (bigint 1) as the studio artist for deterministic tests.
    var angelaApp = db.Database.SqlQueryRaw<Guid>("SELECT app_id AS \"Value\" FROM public.makeup_artists WHERE id = 1").Single();
    configValues["Database:ArtistId"] = angelaApp.ToString();
    config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
    // Full-week availability for the test artist only (isolated DB).
    db.Database.ExecuteSqlRaw("DELETE FROM public.artist_availability WHERE artist_id = 1");
    db.Database.ExecuteSqlRaw("INSERT INTO public.artist_availability(artist_id, weekday, start_time, end_time) SELECT 1, g, time '08:00', time '18:00' FROM generate_series(0,6) g");
    var store = new PublicStudioStore(db, http, env, config, new LocalAppFileStore(env));
    using (var unavailable = db.Database.BeginTransaction())
    {
        db.Database.ExecuteSqlRaw("ALTER FUNCTION public.mvc_occupied_intervals(bigint,timestamptz,timestamptz,uuid) RENAME TO mvc_occupied_intervals_hidden");
        var denied = false;
        try { store.OccupiedIntervals(DateTime.Today, DateTime.Today.AddDays(1), null); }
        catch (InvalidOperationException) { denied = true; }
        unavailable.Rollback();
        Check(denied, "repository fails closed when occupied-interval interface is missing");
    }
    var workflow = new BookingService(store, store, store, store, store);
    Check(store.Services.Count == 3, $"Reads 3 legacy services via views (found {store.Services.Count})");
    Check(store.Settings.OpenHour == 8 && store.Settings.BufferMinutes == 45, "Reads studio settings");
    Check(store.Bookings.Count == 1 && store.Bookings[0].StoredReference == "LEGACY-001", "Legacy cancelled reservation visible with original reference");
    Check(store.Bookings[0].Total == 600, "Legacy total preserved (600, not recomputed)");
    var legacyAdmin = store.Users.SingleOrDefault(u => u.Role == "Admin");
    Check(legacyAdmin != null, "Legacy admin visible (login will require password reset)");
    var admin = store.AddUser("Studio", "Artist", "artist@example.test", "ArtistPassword123!", "Admin", 35, "", "Studio address on file");
    var customer = store.AddUser("Maria", "Santos", "maria@example.test", "CustomerPassword123!", "Customer", 25, "Female", "24 Test Street, Barangay");
    if (args.Length == 4)
    {
        var otherMvc = store.AddUser("Other", "Customer", "other.mvc@example.test", "CustomerPassword123!", "Customer", 25, "Female", "25 Test Street");
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE public.account_preferences (
                user_id uuid PRIMARY KEY REFERENCES public.account_identities(id),
                verified_email text NOT NULL DEFAULT '', verified_at timestamptz,
                notifications_enabled boolean NOT NULL DEFAULT true, avatar_url text
            )
            """);
        await MvcBookingHttpChecks.RunAsync(source, temp, angelaApp, customer, otherMvc, admin);
        return;
    }
    Check(store.FindUser("MARIA@example.test")?.Id == customer.Id, "Registration persists, case-insensitive lookup");
    Reject(() => store.AddUser("Other", "Customer", "MARIA@example.test", "CustomerPassword123!", "Customer", 25, "", "24 Test Street"), "Duplicate email rejected");
    Login(admin);
    var soft = store.Services.Single(x => x.Name == "Soft Glam");
    var lash = store.AddOns.FirstOrDefault(x => x.Name == "Lash application");
    Check(lash != null, "Add-ons catalog available");
    // Future weekday 10:00 Manila, 7+ days out.
    var manila = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
    var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, manila).Date.AddDays(7).AddHours(10);
    while (start.DayOfWeek == DayOfWeek.Saturday) start = start.AddDays(1);
    Login(customer);
    var quote = workflow.Quote(soft.Id, [], 1, "auto");
    var beforeCalendar = commands.Count;
    var calendar = workflow.GetAvailability(new DateTime(start.Year, start.Month, 1), quote.DurationMinutes);
    var calendarQueries = commands.Count - beforeCalendar;
    Check(calendar.Days.Count == DateTime.DaysInMonth(start.Year, start.Month) && calendar.Days.Any(d => d.AvailableCount > 0), "Public availability renders the full month with bookable dates");
    Check(calendarQueries <= 12, $"Month availability uses bounded database reads, not one query per time slot ({calendarQueries} commands)");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        var beforeCancelled = commands.Count;
        try { workflow.GetAvailability(new DateTime(start.Year, start.Month, 1), quote.DurationMinutes, cancellationToken: cancelled.Token); throw new Exception("Cancelled availability did not stop."); }
        catch (OperationCanceledException) { Check(commands.Count == beforeCancelled, "Cancelled availability stops before database-backed calendar reads"); }
    }
    Reject(() => store.SubmitBooking(customer.Id, soft.Id, [], start, "24 Test Street", "Quezon City", quote.RequiredDownPayment, "GCash", "NO-CONSENT", null, true, false, 1, "auto"), "Travel reminder acknowledgment required");
    var b = store.SubmitBooking(customer.Id, soft.Id, [], start, "24 Test Street", "Quezon City", quote.RequiredDownPayment, "GCash", "TEST-REF-001", SyntheticProof(1), true, true, 1, "auto");
    Check(b.Status == "Pending Payment" && b.RequiredDownPayment == quote.RequiredDownPayment, "Booking submits as Pending Payment with exact 50% deposit");
    Check(store.Settings.DownPaymentPercent == 50 && b.DownPaymentPercentSnapshot == 50, "Public store reads the 50% default policy and snapshots it per booking");
    Check(b.TravelFee == 0, "New bookings do not snapshot the legacy flat transportation fee");
    Check(db.Database.SqlQueryRaw<decimal>("SELECT required_down_payment_snapshot AS \"Value\" FROM public.reservations WHERE app_id = {0}", b.Id).Single() == quote.RequiredDownPayment, "Reservation stores the booking-time deposit snapshot");
    Check(store.Payments.Single(p => p.BookingId == b.Id).Reference == "TEST-REF-001", "Deposit recorded with reference");
    var oldReview = store.ReviewPayments(b.Id).Single();
    var revisedProof = SyntheticProof(2);
    store.CorrectPayment(b.Id, customer.Id, "TEST-REF-002", revisedProof);
    Check(store.Payments.Single(p => p.BookingId == b.Id).Reference == "TEST-REF-002", "Proof correction persists");
    Login(admin);
    Reject(() => store.ManageBooking(b.Id, "verify", admin.Id, "", 0, ""), "Missing review contract cannot verify");
    Reject(() => store.ManageBooking(b.Id, "verify", admin.Id, "", 0, "", oldReview.Id, oldReview.PersistentReviewRevision, oldReview.PersistentReviewVersion), "Correction-first rejects stale review");
    var review = store.ReviewPayments(b.Id).Single();
    Check(review.Reference == "TEST-REF-002" && review.ProofKey != oldReview.ProofKey && store.ReadAsset(review.ProofKey)?.Bytes.SequenceEqual(File.ReadAllBytes(revisedProof)) == true, "Review identifies corrected reference and exact proof bytes");
    Reject(() => store.ManageBooking(b.Id, "verify", admin.Id, "", 0, "", review.Id, review.PersistentReviewRevision, "bad"), "Malformed review version cannot verify");
    Reject(() => store.ManageBooking(b.Id, "verify", customer.Id, "", 0, "", review.Id, review.PersistentReviewRevision, review.PersistentReviewVersion), "Customer cannot perform Admin verification");
    var retained = store.ReviewPayments(b.Id).Single();
    Check(!retained.Verified && retained.Reference == review.Reference && retained.ProofKey == review.ProofKey
        && retained.PersistentReviewRevision == review.PersistentReviewRevision && retained.PersistentReviewVersion == review.PersistentReviewVersion
        && store.Bookings.Single(x => x.Id == b.Id).Status == "Pending Payment", "Rejected reviews roll back without changing payment or booking");
    var controller = new AdminController(store, workflow, null!, null!, null!) { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http.HttpContext! } };
    Check(controller.Proof(oldReview.Id, b.Id, oldReview.PersistentReviewRevision, oldReview.PersistentReviewVersion) is Microsoft.AspNetCore.Mvc.ConflictObjectResult, "Stale proof link rejected");
    Check(controller.Proof(review.Id, b.Id, review.PersistentReviewRevision, review.PersistentReviewVersion) is Microsoft.AspNetCore.Mvc.FileContentResult exact && exact.FileContents.SequenceEqual(File.ReadAllBytes(revisedProof)), "Current proof link returns exact reviewed proof");
    store.ManageBooking(b.Id, "verify", admin.Id, "", 0, "", review.Id, review.PersistentReviewRevision, review.PersistentReviewVersion);
    store.ManageBooking(b.Id, "verify", admin.Id, "", 0, "", review.Id, review.PersistentReviewRevision, review.PersistentReviewVersion);
    b = store.Bookings.Single(x => x.Id == b.Id);
    Check(b.Status == "Confirmed" && b.VerifiedPaid == b.RequiredDownPayment, "Verification confirms booking, idempotent retry safe");
    Login(customer);
    Reject(() => store.CorrectPayment(b.Id, customer.Id, "AFTER-VERIFY", null), "Verification-first rejects later correction");
    Login(admin);
    // Overlap must fail: same artist, overlapping time.
    Login(customer);
    var overlapQuote = workflow.Quote(soft.Id, [], 1, "auto");
    Reject(() => store.SubmitBooking(customer.Id, soft.Id, [], start.AddMinutes(30), "24 Test Street", "Quezon City", overlapQuote.RequiredDownPayment, "Maya", "OVERLAP-001", null, true, true, 1, "auto"), "Overlapping appointment rejected");
    // Reschedule round-trip.
    store.RequestReschedule(b.Id, customer.Id, start.AddDays(1));
    Check(store.Bookings.Single(x => x.Id == b.Id).RequestedStart != null, "Reschedule request keeps original slot");
    Login(admin);
    store.ManageBooking(b.Id, "reschedule", admin.Id, "", 0, "");
    Check(store.Bookings.Single(x => x.Id == b.Id).RequestedStart == null, "Reschedule approval clears request");
    // Blocks.
    var block = new AvailabilityBlock(Guid.NewGuid(), start.AddDays(2), start.AddDays(2).AddHours(2), "Artist unavailable");
    store.AddBlock(block, admin.Id);
    Check(store.Blocks.Any(x => x.Id == block.Id), "Availability block persists");
    Reject(() => store.AddBlock(new AvailabilityBlock(Guid.NewGuid(), start.AddDays(1).AddMinutes(30), start.AddDays(1).AddMinutes(90), "Overlap"), admin.Id), "Block overlapping confirmed booking rejected");
    store.RemoveBlock(block.Id, admin.Id);
    Check(!store.Blocks.Any(x => x.Id == block.Id), "Block removal persists");
    // Catalog price change must not reprice existing booking.
    var oldTotal = store.Bookings.Single(x => x.Id == b.Id).Total;
    soft.Price += 100; store.SaveCatalog("service", soft, admin.Id);
    Check(store.Bookings.Single(x => x.Id == b.Id).Total == oldTotal, "Historical booking price preserved after catalog edit");
    // Cancel + refund.
    Login(customer);
    store.CancelBooking(b.Id, customer.Id, true);
    b = store.Bookings.Single(x => x.Id == b.Id);
    Check(b.Status == "Cancelled" && b.CancellationFee == 200, "Cancellation applies exactly PHP 200 fee");
    var due = b.RefundDue;
    Login(admin);
    if (due > 0) store.ManageBooking(b.Id, "refund", admin.Id, "", due, "REFUND-TEST-001");
    Check(store.Bookings.Single(x => x.Id == b.Id).Refunded == due, "Manual refund recorded");
    Reject(() => store.ManageBooking(b.Id, "refund", admin.Id, "", 1, "REFUND-OVER"), "Refund overpayment rejected");
    // Archive vs delete.
    store.DeleteCatalog("service", soft, admin.Id);
    Check(store.Services.Single(x => x.Id == soft.Id).Archived, "Referenced service archived, not deleted");
    // Notifications + audit.
    Login(customer);
    Check(store.Notifications.Count > 0, "Customer notifications persisted");
    Check(store.AuditLogs.Count > 0, "Audit trail persisted");
    // Isolation.
    var other = store.AddUser("Other", "Customer", "other@example.test", "CustomerPassword123!", "Customer", 25, "", "24 Other Street");
    Login(other);
    Check(!store.Bookings.Any(x => x.Id == b.Id), "Customer cannot read another customer's booking");
    // Legacy preservation final.
    Check(db.Database.SqlQueryRaw<long>("SELECT count(*)::bigint AS \"Value\" FROM public.customers").Single() == 3, "Customer rows preserved plus test accounts (1 legacy + 2 new)");
    Check(db.Database.SqlQueryRaw<long>("SELECT count(*)::bigint AS \"Value\" FROM public.admin_users").Single() == 2, "Admin rows preserved plus test admin (1 legacy + 1 new)");
    Check(db.Database.SqlQueryRaw<string>("SELECT reference AS \"Value\" FROM public.reservations WHERE reference='LEGACY-001'").Single() == "LEGACY-001", "Legacy reference untouched");
    // A1 regression: password reset/change must preserve is_active and never reactivate.
    static bool UsesPassword(AppUser u, string password) =>
        new PasswordHasher<AppUser>().VerifyHashedPassword(u, u.PasswordHash, password) != PasswordVerificationResult.Failed;
    var activeCustomer = store.AddUser("Reset", "Active", "reset.active@example.test", "Original1Pass", "Customer", 25, "", "1 Reset Street");
    store.ResetPassword("reset.active@example.test", "Changed1Pass");
    var activeCustomerRow = store.FindUser("reset.active@example.test")!;
    Check(activeCustomerRow.IsActive && db.Database.SqlQueryRaw<bool>("SELECT is_active AS \"Value\" FROM public.customers WHERE app_id={0}", activeCustomer.Id).Single(), "Active customer stays active after password reset");
    Check(UsesPassword(activeCustomerRow, "Changed1Pass") && !UsesPassword(activeCustomerRow, "Original1Pass"), "Password reset still updates the active customer's credential");
    var disabledCustomer = store.AddUser("Reset", "Disabled", "reset.disabled@example.test", "Original2Pass", "Customer", 25, "", "2 Reset Street");
    db.Database.ExecuteSqlInterpolated($"UPDATE public.customers SET is_active=false WHERE app_id={disabledCustomer.Id}");
    store.ResetPassword("reset.disabled@example.test", "Changed2Pass");
    Check(!db.Database.SqlQueryRaw<bool>("SELECT is_active AS \"Value\" FROM public.customers WHERE app_id={0}", disabledCustomer.Id).Single(), "Disabled customer stays disabled after password reset");
    var disabledCustomerRow = store.FindUser("reset.disabled@example.test")!;
    Check(!disabledCustomerRow.IsActive, "Disabled customer fails the active-account gate the login path enforces");
    Check(UsesPassword(disabledCustomerRow, "Changed2Pass"), "Disabled customer credential updates without reactivation");
    Check(db.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM public.account_identities WHERE id={0}", disabledCustomer.Id).Single() == disabledCustomer.Id, "Identity link row preserved across a disabled password reset");
    var disabledAdmin = store.AddUser("Reset", "Admin", "reset.admin@example.test", "Original3Pass", "Admin", 35, "", "3 Reset Street");
    db.Database.ExecuteSqlInterpolated($"UPDATE public.admin_users SET is_active=false WHERE app_id={disabledAdmin.Id}");
    store.ResetPassword("reset.admin@example.test", "Changed3Pass");
    Check(!db.Database.SqlQueryRaw<bool>("SELECT is_active AS \"Value\" FROM public.admin_users WHERE app_id={0}", disabledAdmin.Id).Single(), "Disabled admin stays disabled after password reset");
    Check(!store.FindUser("reset.admin@example.test")!.IsActive, "Disabled admin fails the active-account gate the login path enforces");
    db.Database.ExecuteSqlRaw("CREATE ROLE bootstrap_filtered_test NOLOGIN; GRANT USAGE ON SCHEMA public TO bootstrap_filtered_test");
    using (var filtered = new PublicMakeupDbContext(new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(source).Options))
    {
        filtered.Database.OpenConnection();
        try
        {
            filtered.Database.ExecuteSqlRaw("SET ROLE bootstrap_filtered_test");
            var restrictedStore = new PublicStudioStore(filtered, new HttpContextAccessor(), env, config, new LocalAppFileStore(env));
            var visibilityDenied = false;
            try { restrictedStore.CreateInitialSuperAdmin("Test", "Owner", "filtered@example.test", "OwnerPassword123!"); }
            catch (InvalidOperationException e) when (e.Message == "Initial SuperAdmin setup requires unfiltered account visibility.") { visibilityDenied = true; }
            Check(visibilityDenied, "initial SuperAdmin setup rejects RLS-filtered account visibility");
        }
        finally { filtered.Database.ExecuteSqlRaw("RESET ROLE"); filtered.Database.CloseConnection(); }
    }
    Reject(() => store.CreateInitialSuperAdmin("Test", "Owner", "reset.active@example.test", "OwnerPassword123!"),
        "initial SuperAdmin refuses an existing Customer email");
    using var startBootstrap = new ManualResetEventSlim(false);
    Task<bool> Bootstrap(int n) => Task.Run(() =>
    {
        using var context = new PublicMakeupDbContext(new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(source).Options);
        var candidate = new PublicStudioStore(context, new HttpContextAccessor(), env, config, new LocalAppFileStore(env));
        startBootstrap.Wait();
        try { candidate.CreateInitialSuperAdmin("Test", "Owner", $"owner{n}@example.test", "OwnerPassword123!"); return true; }
        catch (InvalidOperationException e) when (e.Message.StartsWith("A SuperAdmin already exists.")) { return false; }
    });
    var attempts = new[] { Bootstrap(1), Bootstrap(2) };
    startBootstrap.Set();
    var outcomes = await Task.WhenAll(attempts);
    Check(outcomes.Count(x => x) == 1 &&
        db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.admin_users WHERE role='SuperAdmin'").Single() == 1,
        "concurrent private bootstrap attempts create exactly one SuperAdmin");
    Reject(() => store.CreateInitialSuperAdmin("Test", "Owner", "owner3@example.test", "OwnerPassword123!"),
        "initial SuperAdmin setup refuses a later attempt");
    var disabledSuper = store.CreateElevatedUser("Reset", "Super", "reset.super@example.test", "Original4Pass", "SuperAdmin", 40, "", "4 Reset Street");
    db.Database.ExecuteSqlInterpolated($"UPDATE public.admin_users SET is_active=false WHERE app_id={disabledSuper.Id}");
    store.ResetPassword("reset.super@example.test", "Changed4Pass");
    var disabledSuperRow = store.FindUser("reset.super@example.test")!;
    Check(!db.Database.SqlQueryRaw<bool>("SELECT is_active AS \"Value\" FROM public.admin_users WHERE app_id={0}", disabledSuper.Id).Single(), "Disabled SuperAdmin stays disabled after password reset");
    Check(disabledSuperRow.Role == "SuperAdmin" && !disabledSuperRow.IsActive, "SuperAdmin role preserved while remaining disabled after reset");
    Console.WriteLine($"All {checks} public store checks passed.");
}
catch (Exception e) { Console.Error.WriteLine(e.ToString()); Environment.ExitCode = 1; }
finally { Console.WriteLine("Synthetic proof artifacts retained: " + temp); }

sealed class TestEnv : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "PublicStoreChecks";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}

sealed class CountingCommands : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public int Count;
    public override System.Data.Common.DbDataReader ReaderExecuted(System.Data.Common.DbCommand command,
        Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData, System.Data.Common.DbDataReader result)
    {
        Interlocked.Increment(ref Count);
        return result;
    }
}
