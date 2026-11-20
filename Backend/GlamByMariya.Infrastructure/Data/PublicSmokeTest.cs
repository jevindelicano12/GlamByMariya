using System.Security.Claims;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GlamByMariya.Infrastructure.Data;

// Legacy write-capable smoke test. Do not run against the real Supabase project:
// it creates persistent accounts, booking, payment, proof and history records.
// It is not a disposable-target-guarded integration runner.
public static class PublicSmokeTest
{
    public static async Task RunAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var pdb = scope.ServiceProvider.GetRequiredService<PublicMakeupDbContext>();
        var http = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        // Smoke artist: Angela Cruz (bigint 1). The app store requires an explicit
        // artist choice when several are active, so the smoke configures one
        // explicitly rather than letting the store guess.
        var smokeArtistApp = pdb.Database.SqlQueryRaw<Guid>("SELECT app_id AS \"Value\" FROM public.makeup_artists WHERE id=1 AND is_active").SingleOrDefault();
        if (smokeArtistApp == Guid.Empty) throw new InvalidOperationException("SMOKE FAIL: expected active artist id=1 for smoke");
        var smokeConfig = new ConfigurationBuilder().AddConfiguration(app.Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ArtistId"] = smokeArtistApp.ToString() }).Build();
        var store = new PublicStudioStore(pdb, http, env, smokeConfig,
            scope.ServiceProvider.GetRequiredService<GlamByMariya.Infrastructure.Integrations.IAppFileStore>());
        var workflow = new BookingService(store, store, store, store, store);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        int checks = 0;
        void Check(bool c, string m) { if (!c) throw new InvalidOperationException("SMOKE FAIL: " + m); checks++; Console.WriteLine("SMOKE PASS: " + m); }
        void Reject(Action a, string m) { try { a(); } catch (InvalidOperationException) { Check(true, m); return; } throw new InvalidOperationException("SMOKE FAIL (expected rejection): " + m); }
        void Login(AppUser u) => http.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
                new Claim(ClaimTypes.Role, u.Role),
                new Claim(ClaimTypes.Email, u.Email)
            }, "smoke"))
        };

        // 0. Baseline: legacy data intact, integration objects present.
        var legacyBefore = pdb.Database.SqlQueryRaw<LegacyReservation>("SELECT reference AS \"Reference\", total_price AS \"Total\", status AS \"Status\" FROM public.reservations").Single();
        Check(true, $"Legacy reservation present before smoke ({legacyBefore.Reference}, ₱{legacyBefore.Total:N2}, {legacyBefore.Status})");
        var views = pdb.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_views WHERE schemaname='public' AND viewname LIKE 'mvc_%'").Single();
        Check(views >= 10, $"Integration views present ({views})");
        Check(store.Services.Count == 3, "Customer catalog reads 3 legacy services");
        Check(smokeArtistApp != Guid.Empty, "Artist Angela Cruz active");

        // 1. Registration + login hash verification (isolated test customer).
        var customerEmail = $"smoke.customer.{stamp}@example.test";
        var customerPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var customer = store.AddUser("Smoke", "Customer", customerEmail, customerPassword, "Customer", 25, "Others", "123 Smoke Street, Test Barangay");
        Check(store.FindUser(customerEmail.ToUpperInvariant())?.Id == customer.Id, "Customer registration persists (case-insensitive lookup)");
        var hasherOk = new PasswordHasher<AppUser>().VerifyHashedPassword(customer, customer.PasswordHash, customerPassword) != PasswordVerificationResult.Failed;
        Check(hasherOk, "Password hash verifies (login flow)");
        Reject(() => store.AddUser("Dup", "Customer", customerEmail, customerPassword, "Customer", 25, "", "123 Smoke Street, Test Barangay"), "Duplicate email rejected");

        // 2. Admin account for verification steps.
        var adminEmail = $"smoke.admin.{stamp}@example.test";
        var admin = store.AddUser("Smoke", "Admin", adminEmail, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), "Admin", 35, "", "Studio address on file");
        Check(store.FindUser(adminEmail)?.Role == "Admin", "Admin account persists with role");

        // 3. Service catalog create/delete round-trip (unreferenced item removed).
        var probe = new CatalogItem { Name = $"SMOKE-TEST Service {stamp}", Description = "Smoke probe", Price = 100, Minutes = 30 };
        store.SaveCatalog("addon", probe, admin.Id);
        Check(store.AddOns.Any(x => x.Id == probe.Id), "Test add-on visible to customers immediately");
        store.DeleteCatalog("addon", probe, admin.Id);
        Check(!store.AddOns.Any(x => x.Id == probe.Id), "Unreferenced test add-on deleted");

        // 4. Booking lifecycle on a real legacy service (Soft Glam, individual).
        Login(admin);
        var soft = store.Services.Single(x => x.Name == "Soft Glam");
        // Use Angela's actual weekly availability from the live database.
        var openDays = pdb.Database.SqlQueryRaw<int>("SELECT DISTINCT weekday AS \"Value\" FROM public.artist_availability WHERE artist_id=1").ToList();
        Check(openDays.Count > 0, $"Smoke artist has weekly availability ({string.Join(",", openDays)})");
        var manila = TimeZoneInfo.FindSystemTimeZoneById(store.Settings.TimeZone);
        var day = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, manila).Date.AddDays(7);
        while (!openDays.Contains((int)day.DayOfWeek)) day = day.AddDays(1);
        var start = day.AddHours(10);
        Login(customer);
        var quote = workflow.Quote(soft.Id, [], 1, "auto");
        Check(quote.DownPaymentPercent == store.Settings.DownPaymentPercent && quote.RequiredDownPayment == Math.Round(quote.FinalTotal * quote.DownPaymentPercent / 100m, 2, MidpointRounding.AwayFromZero), $"Quote applies the configured down payment percentage (₱{quote.RequiredDownPayment:N2} of ₱{quote.FinalTotal:N2} at {quote.DownPaymentPercent:N0}%)");
        var payRef = $"SMOKE-{stamp}-001";
        var proofDirectory = Path.Combine(env.ContentRootPath, "App_Data", "Proofs");
        Directory.CreateDirectory(proofDirectory);
        var syntheticProof = Path.Combine(proofDirectory, Guid.NewGuid() + ".png");
        File.WriteAllBytes(syntheticProof, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6cWQAAAAASUVORK5CYII="));
        var b = store.SubmitBooking(customer.Id, soft.Id, [], start, "123 Smoke Street, Test Barangay", "Quezon City", quote.RequiredDownPayment, "GCash", payRef, syntheticProof, true, true, 1, "auto");
        Check(b.Status == "Pending Payment", "Booking created as Pending Payment");
        Check(store.Payments.Single(p => p.BookingId == b.Id).Reference == payRef, "Deposit recorded with reference");
        store.CorrectPayment(b.Id, customer.Id, payRef + "-C", null);
        Check(store.Payments.Single(p => p.BookingId == b.Id).Reference == payRef + "-C", "Proof/reference correction persists");
        Login(admin);
        var reviewedPayment = store.ReviewPayments(b.Id).Single();
        store.ManageBooking(b.Id, "verify", admin.Id, "", 0, "", reviewedPayment.Id, reviewedPayment.PersistentReviewRevision, reviewedPayment.PersistentReviewVersion);
        b = store.Bookings.Single(x => x.Id == b.Id);
        Check(b.Status == "Confirmed" && b.VerifiedPaid == b.RequiredDownPayment, "Admin verification confirms booking");
        Login(customer);
        Reject(() => store.SubmitBooking(customer.Id, soft.Id, [], start.AddMinutes(30), "123 Smoke Street, Test Barangay", "Quezon City", quote.RequiredDownPayment, "Maya", payRef + "-OVERLAP", null, true, true, 1, "auto"), "Overlapping appointment rejected");
        store.RequestReschedule(b.Id, customer.Id, start.AddDays(7));
        Check(store.Bookings.Single(x => x.Id == b.Id).RequestedStart != null, "Reschedule request keeps original slot");
        Login(admin);
        store.ManageBooking(b.Id, "reschedule", admin.Id, "", 0, "");
        Check(store.Bookings.Single(x => x.Id == b.Id).RequestedStart == null, "Reschedule approval moves slot atomically");

        // 5. Availability block round-trip + overlap guard.
        var moved = store.Bookings.Single(x => x.Id == b.Id).Start;
        var block = new AvailabilityBlock(Guid.NewGuid(), moved.AddDays(2), moved.AddDays(2).AddHours(2), $"SMOKE-TEST block {stamp}");
        store.AddBlock(block, admin.Id);
        Check(store.Blocks.Any(x => x.Id == block.Id), "Availability block persists");
        Reject(() => store.AddBlock(new AvailabilityBlock(Guid.NewGuid(), moved.AddMinutes(30), moved.AddMinutes(90), "Overlap"), admin.Id), "Block overlapping confirmed booking rejected");
        store.RemoveBlock(block.Id, admin.Id);
        Check(!store.Blocks.Any(x => x.Id == block.Id), "Block removal persists");

        // 6. Cancellation + manual refund ledger.
        Login(customer);
        store.CancelBooking(b.Id, customer.Id, true);
        b = store.Bookings.Single(x => x.Id == b.Id);
        Check(b.Status == "Cancelled" && b.CancellationFee == 200, "Cancellation applies exactly ₱200 fee");
        Check(b.RefundDue == Math.Max(0, b.VerifiedPaid - 200), $"Refund due ₱{b.RefundDue:N2} honors verified-paid-minus-fee rule");
        Login(admin);
        if (b.RefundDue > 0) store.ManageBooking(b.Id, "refund", admin.Id, "", b.RefundDue, $"SMOKE-REFUND-{stamp}");
        Check(store.Bookings.Single(x => x.Id == b.Id).Refunded == b.RefundDue, "Manual refund recorded without overpayment");
        Reject(() => store.ManageBooking(b.Id, "refund", admin.Id, "", 1, "SMOKE-OVER"), "Refund overpayment rejected");

        // 7. Isolation + history + legacy preservation.
        var other = store.AddUser("Smoke", "Other", $"smoke.other.{stamp}@example.test", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), "Customer", 25, "", "456 Other Street, Test Barangay");
        Login(other);
        Check(!store.Bookings.Any(x => x.Id == b.Id) && store.Payments.Count(p => p.BookingId == b.Id) == 0, "Customer isolation enforced on bookings/payments");
        Reject(() => store.CancelBooking(b.Id, other.Id, true), "Another customer cannot cancel the booking");
        Login(customer);
        Check(store.Notifications.Count > 0, "Customer notifications persisted");
        Check(store.AuditLogs.Count > 0, "Audit trail persisted");
        Check(pdb.Database.SqlQueryRaw<string>("SELECT reference AS \"Value\" FROM public.reservations WHERE reference={0}", legacyBefore.Reference).Single() == legacyBefore.Reference, "Legacy reservation untouched by smoke");
        Check(pdb.Database.SqlQueryRaw<decimal>("SELECT total_price AS \"Value\" FROM public.reservations WHERE reference={0}", legacyBefore.Reference).Single() == legacyBefore.Total, $"Legacy total untouched ({legacyBefore.Total})");
        Check(pdb.Database.SqlQueryRaw<string>("SELECT status AS \"Value\" FROM public.reservations WHERE reference={0}", legacyBefore.Reference).Single() == legacyBefore.Status, "Legacy status untouched");
        Console.WriteLine($"SMOKE SUMMARY: customer={customer.Id} admin={admin.Id} booking={b.Id} reference={b.Reference} refundDue={b.RefundDue:N2}. All {checks} live smoke checks passed.");
        await Task.CompletedTask;
    }

    private sealed class LegacyReservation
    {
        public string Reference { get; set; } = "";
        public decimal Total { get; set; }
        public string Status { get; set; } = "";
    }
}
