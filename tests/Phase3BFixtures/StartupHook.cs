using System.Text.Json;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ImageMagick;

[assembly: HostingStartup(typeof(Phase3BFixtureStartup))]

// Loaded only by the isolated browser test process, never by the application build.
public static class StartupHook
{
    public static void Initialize() => Environment.SetEnvironmentVariable("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "Phase3BFixtures");
}

public sealed class Phase3BFixtureStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder) => builder.ConfigureServices(services => services.AddTransient<IStartupFilter, Phase3BFixtureFilter>());
}

public sealed class Phase3BFixtureFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var store = app.ApplicationServices.GetRequiredService<DemoStore>();
        var root = Environment.GetEnvironmentVariable("PHASE3B_FIXTURE_ROOT") ?? throw new InvalidOperationException("Missing isolated fixture root.");
        var proofDir = Path.Combine(root, "App_Data", "Proofs"); Directory.CreateDirectory(proofDir);
        var proof = Path.Combine(proofDir, "fixture.png");
        File.WriteAllBytes(proof, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lFcAAAAASUVORK5CYII="));
        var customer = store.Users.Single(u => u.Role == Roles.Customer);
        var owner = store.Users.Single(u => u.Role == Roles.SuperAdmin);
        var s = store.Services.First(); var snapshot = new PriceSnapshot(s.Id, s.Name, s.Price, s.Minutes);
        var ids = new Dictionary<string, Guid>();
        var dates = new Dictionary<string, string>();
        foreach (var type in new[] { "individual", "same", "mixed" })
        foreach (var scenario in new[] { "past", "future", "cancel", "reject", "unverified" })
        {
            var mixed = type == "mixed" ? new[] { new MixedPersonQuote(snapshot, [], snapshot.Price, snapshot.Minutes), new MixedPersonQuote(snapshot, [], snapshot.Price, snapshot.Minutes) } : Array.Empty<MixedPersonQuote>();
            var booking = new Booking { CustomerId = customer.Id, CustomerName = customer.Name, Service = snapshot, People = type == "individual" ? 1 : 2, Package = type == "same" ? "group" : "auto", MixedPeople = mixed, Start = DateTime.Today.AddDays(scenario == "past" ? -3 : 40 + ids.Count).AddHours(8), BufferMinutesSnapshot = 45, Address = "Synthetic address", Area = "Quezon City", Status = scenario == "reject" ? "Rejected" : scenario == "unverified" ? "Pending Payment" : "Confirmed" };
            var verified = scenario != "unverified" && scenario != "reject";
            var payment = new Payment { BookingId = booking.Id, Amount = booking.RequiredDownPayment, Method = "GCash", Reference = "FIXTURE-" + type + "-" + scenario, ProofPath = proof, Verified = verified, VerifiedBy = verified ? owner.Id : null, VerifiedAt = verified ? DateTime.Now.AddDays(-5) : null };
            booking.VerifiedPaid = verified ? payment.Amount : 0; booking.PaymentStatus = verified ? "Down Payment Verified" : "Pending Verification";
            store.Bookings.Add(booking); store.Payments.Add(payment); ids.Add(type + "-" + scenario, booking.Id); dates.Add(type + "-" + scenario, booking.Start.ToString("yyyy-MM-ddTHH:mm"));
        }
        if (Environment.GetEnvironmentVariable("OWNERSHIP_BROWSER_FIXTURES") == "true")
        {
            // Isolated test process only: no public registration or persisted account.
            var other = store.AddUser("Second", "Synthetic", "ownership-b@example.test", "SyntheticCustomerB123!", Roles.Customer);
            foreach (var (label, actor, day) in new[] { ("ownership-a", customer, 110), ("ownership-b", other, 112) })
            {
                var ownerProof = Path.Combine(proofDir, label + ".png");
                using (var syntheticImage = new MagickImage(label == "ownership-a" ? MagickColors.Red : MagickColors.Blue, 8, 8))
                {
                    syntheticImage.Format = MagickFormat.Png;
                    File.WriteAllBytes(ownerProof, syntheticImage.ToByteArray());
                }
                var booking = new Booking { CustomerId = actor.Id, CustomerName = actor.Name, Service = snapshot,
                    Start = DateTime.Today.AddDays(day).AddHours(8), BufferMinutesSnapshot = 45,
                    Address = "Synthetic " + label + " address", Area = "Quezon City", Status = "Confirmed" };
                var payment = new Payment { BookingId = booking.Id, Amount = booking.RequiredDownPayment,
                    Method = "GCash", Reference = "FIXTURE-" + label, ProofPath = ownerProof,
                    Verified = true, VerifiedBy = owner.Id, VerifiedAt = DateTime.Today };
                booking.VerifiedPaid = payment.Amount; booking.PaymentStatus = "Down Payment Verified";
                store.Bookings.Add(booking); store.Payments.Add(payment); ids.Add(label, booking.Id);
            }
            foreach (var (status, slug) in new[] { ("Pending Payment", "pending"), ("Rejected", "rejected"), ("Cancelled", "cancelled"), ("Completed", "completed"), ("No-show", "no-show"), ("Incomplete", "incomplete") })
            foreach (var (label, actor, day) in new[] { ("a", customer, 160), ("b", other, 162) })
            {
                var key = $"ownership-{slug}-{label}";
                var stateProof = proof;
                if (slug == "pending")
                {
                    stateProof = Path.Combine(proofDir, key + ".png");
                    using var syntheticImage = new MagickImage(label == "a" ? MagickColors.Green : MagickColors.Yellow, 8, 8);
                    syntheticImage.Format = MagickFormat.Png;
                    File.WriteAllBytes(stateProof, syntheticImage.ToByteArray());
                }
                var booking = new Booking { CustomerId = actor.Id, CustomerName = actor.Name, Service = snapshot,
                    Start = DateTime.Today.AddDays(status is "Completed" or "No-show" or "Incomplete" ? -10 : day).AddHours(8),
                    BufferMinutesSnapshot = 45, Address = $"Synthetic {key} address", Area = "Quezon City", Status = status };
                var verified = status is "Cancelled" or "Completed" or "No-show" or "Incomplete";
                var payment = new Payment { BookingId = booking.Id, Amount = booking.RequiredDownPayment, Method = "GCash",
                    Reference = $"FIXTURE-{key}", ProofPath = stateProof, Verified = verified,
                    VerifiedBy = verified ? owner.Id : null, VerifiedAt = verified ? DateTime.Today.AddDays(-12) : null };
                booking.VerifiedPaid = verified ? payment.Amount : 0;
                booking.PaymentStatus = verified ? "Down Payment Verified" : "Pending Verification";
                store.Bookings.Add(booking); store.Payments.Add(payment); ids.Add(key, booking.Id);
            }
        }
        File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(ids));
        File.WriteAllText(Path.Combine(root, "fixture-dates.json"), JsonSerializer.Serialize(dates));
        if (Environment.GetEnvironmentVariable("OWNERSHIP_BROWSER_FIXTURES") == "true")
        {
            // Private test artifact, never an HTTP endpoint. Captures hidden state
            // (including an uncommitted reschedule offer) after booking requests.
            app.Use(async (context, nextRequest) =>
            {
                await nextRequest();
                var tag = context.Request.Headers["X-Ownership-Test-Case"].ToString();
                if (!string.IsNullOrEmpty(tag) && (context.Request.Path.StartsWithSegments("/Bookings") ||
                    (Environment.GetEnvironmentVariable("REVIEW_BROWSER_FIXTURES") == "true" && context.Request.Path == "/Admin/Action")))
                {
                    lock (store.Gate)
                    {
                        var targets = ids.Where(x => x.Key.StartsWith("ownership-", StringComparison.Ordinal)).Select(x => x.Value).ToArray();
                        var snapshot = targets.Select(id => new { booking = store.Bookings.Single(b => b.Id == id), payments = store.Payments.Where(p => p.BookingId == id).ToArray() }).ToArray();
                        File.WriteAllText(Path.Combine(root, "ownership-state.json"), JsonSerializer.Serialize(new { tag, snapshot }));
                        if (Environment.GetEnvironmentVariable("REVIEW_BROWSER_FIXTURES") == "true")
                            File.WriteAllText(Path.Combine(root, "review-state.json"), JsonSerializer.Serialize(new { tag, bookings = store.Bookings, payments = store.Payments, notifications = store.Notifications, audit = store.AuditLogs }));
                    }
                }
            });
        }
        next(app);
    };
}
