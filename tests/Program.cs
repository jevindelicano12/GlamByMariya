using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Npgsql;

var store = new DemoStore();
var workflow = new BookingService(store, store, store, store, store);
var customer = store.Users.First(u => u.Role == "Customer");
var admin = store.Users.First(u => u.Role == "Admin");
var workflowOwner = store.Users.First(u => u.Role == "SuperAdmin");
var service = store.Services[0];
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine($"PASS: {name}"); }
void Reject(Action action, string name) { try { action(); } catch (InvalidOperationException) { Check(true, name); return; } throw new Exception($"Expected rejection: {name}"); }
if (args.Contains("--mixed-read-model-only"))
{
    var bookingId = Guid.NewGuid(); var ownerId = Guid.NewGuid();
    var bridal = Guid.Parse("41c611af-c52c-446a-ae01-1233acf20f32");
    var soft = Guid.NewGuid(); var addon = Guid.NewGuid();
    var header = new PreparedPersistentBooking.MixedHeader(bookingId, ownerId, true, 2, 4700, 250, 240, 15);
    var people = new[] {
        new PreparedPersistentBooking.MixedPersonRow(bookingId, 2, soft, false, "Old Soft", 1200, 90),
        new PreparedPersistentBooking.MixedPersonRow(bookingId, 1, bridal, true, "Old Bridal", 3500, 150)
    };
    var addons = new[] { new PreparedPersistentBooking.MixedAddonRow(bookingId, 1, addon, "Old Lash", 250, 15) };
    var owner = new AppUser { Id=ownerId, Role=Roles.Customer, IsActive=true };
    var super = new AppUser { Id=Guid.NewGuid(), Role=Roles.SuperAdmin, IsActive=true };
    IReadOnlyList<MixedPersonQuote> Read(AppUser actor, PreparedPersistentBooking.MixedHeader h,
        IReadOnlyList<PreparedPersistentBooking.MixedPersonRow> p, IReadOnlyList<PreparedPersistentBooking.MixedAddonRow> a) =>
        PreparedPersistentBooking.ReconstructMixed(actor, h, bridal, p, a);
    var result = Read(owner, header, people, addons);
    Check(result.Count==2 && result[0].Service.ItemId==bridal && result[0].Service.Name=="Old Bridal" &&
        result[0].Service.Price==3500 && result[0].DurationMinutes==165 && result[0].Total==3750 &&
        result[0].AddOns.Single().Name=="Old Lash" && result[1].Service.Name=="Old Soft",
        "sealed snapshot reconstructs historical per-person prices, durations and add-ons in order");
    Check(Read(super, header, people, addons).Count==2, "active SuperAdmin can reconstruct");
    Reject(() => Read(new AppUser { Id=Guid.NewGuid(), Role=Roles.Customer }, header, people, addons), "other Customer rejected");
    Reject(() => Read(new AppUser { Id=ownerId, Role=Roles.Customer, IsActive=false }, header, people, addons), "inactive owner rejected");
    Reject(() => Read(new AppUser { Id=Guid.NewGuid(), Role=Roles.Admin }, header, people, addons), "ordinary Admin rejected");
    Reject(() => Read(new AppUser { Id=Guid.NewGuid(), Role=Roles.SuperAdmin, IsActive=false }, header, people, addons), "inactive SuperAdmin rejected");
    Reject(() => Read(owner, header with { Sealed=false }, people, addons), "unsealed snapshot rejected");
    Reject(() => Read(owner, header with { ServiceSubtotal=4701 }, people, addons), "changed price aggregate rejected");
    Reject(() => Read(owner, header with { AddonMinutes=16 }, people, addons), "changed duration aggregate rejected");
    Reject(() => Read(owner, header, [people[0], people[0]], addons), "duplicate person position rejected");
    Reject(() => Read(owner, header, [people[0], people[1] with { IsBridal=false }], addons), "Bridal classification mismatch rejected");
    Reject(() => Read(owner, header, [people[1], people[1] with { Position=2 }], addons), "second Bridal selection rejected");
    Reject(() => Read(owner, header, people, [addons[0], addons[0]]), "duplicate per-person add-on rejected");
    Reject(() => Read(owner, header, people, [addons[0] with { BookingId=Guid.NewGuid() }]), "cross-booking add-on rejected");
    var mixed = new Booking { Id=bookingId, CustomerId=ownerId, MixedPeople=result };
    var persistent = System.Reflection.DispatchProxy.Create<IPersistentStudioStore, PersistentQuoteProxy>();
    var proxy = (PersistentQuoteProxy)persistent;
    var ordinary = new AppUser { Id=Guid.NewGuid(), Role=Roles.Admin, IsActive=true };
    proxy.TestUsers=[owner, super, ordinary];
    proxy.TestBookings=[mixed];
    var claim = new Payment { BookingId=bookingId, ProofKey="Proofs/synthetic.png" };
    proxy.TestPayments=[claim];
    var flow = new BookingService(persistent, persistent, persistent, persistent, persistent);
    Reject(() => flow.AdminAction(mixed, "verify", super.Id, "", 0, ""), "persistent mixed Admin action remains gated");
    Reject(() => flow.Cancel(mixed, owner.Id, true), "persistent mixed cancellation remains gated");
    Reject(() => flow.CorrectProof(mixed, owner.Id, "SYNTHETIC", null), "persistent mixed correction remains gated");
    var adminController = new AdminController(persistent, flow, null!, null!, null!, null!);
    var customerController = new BookingsController(persistent, flow, null!, null!, null!, null!);
    void Actor(Microsoft.AspNetCore.Mvc.Controller controller, AppUser user) =>
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier,user.Id.ToString()),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role,user.Role)],"synthetic"))
            }
        };
    Actor(adminController, ordinary);
    Check(adminController.Details(bookingId) is Microsoft.AspNetCore.Mvc.ForbidResult &&
          adminController.Action(bookingId,"reject") is Microsoft.AspNetCore.Mvc.ForbidResult &&
          adminController.Proof(claim.Id) is Microsoft.AspNetCore.Mvc.ForbidResult,
        "ordinary Admin cannot access mixed details, action or proof");
    Actor(adminController, super);
    Check(adminController.Details(bookingId) is Microsoft.AspNetCore.Mvc.NotFoundResult &&
          adminController.Action(bookingId,"reject") is Microsoft.AspNetCore.Mvc.NotFoundResult &&
          adminController.Proof(claim.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult,
        "persistent mixed details, action and proof stay closed even to SuperAdmin");
    Actor(customerController, owner);
    Check(customerController.Details(bookingId) is Microsoft.AspNetCore.Mvc.NotFoundResult &&
          customerController.Proof(claim.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult,
        "persistent mixed Customer detail and proof stay closed");
    Actor(customerController, new AppUser { Id=Guid.NewGuid(), Role=Roles.Customer });
    Check(customerController.Details(bookingId) is Microsoft.AspNetCore.Mvc.NotFoundResult &&
          customerController.Proof(claim.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult,
        "another Customer cannot read mixed detail or proof");
    var standard = new Booking { Id=Guid.NewGuid(), CustomerId=ownerId };
    proxy.TestBookings=[mixed, standard];
    claim.ReviewStatus="Correction Requested";
    proxy.TestPayments.Add(new Payment { BookingId=standard.Id, ReviewStatus="Correction Requested" });
    Actor(adminController, ordinary);
    adminController.Index(null);
    var corrections = (HashSet<Guid>)adminController.ViewBag.CorrectionBookingIds;
    Check(corrections.SetEquals([standard.Id]), "ordinary Admin list includes only authorized standard correction status");
    Actor(adminController, super);
    adminController.Index(null);
    corrections = (HashSet<Guid>)adminController.ViewBag.CorrectionBookingIds;
    Check(corrections.SetEquals([standard.Id]), "persistent mixed correction remains hidden even from SuperAdmin lists");
    var demoMixed = new Booking { Id=Guid.NewGuid(), CustomerId=customer.Id, MixedPeople=result };
    store.Bookings.Add(demoMixed);
    store.Payments.Add(new Payment { BookingId=demoMixed.Id, ReviewStatus="Correction Requested" });
    var demoAdminController = new AdminController(store, workflow, null!, null!, null!, null!);
    Actor(demoAdminController, admin);
    demoAdminController.Index(null);
    Check(!((HashSet<Guid>)demoAdminController.ViewBag.CorrectionBookingIds).Contains(demoMixed.Id),
        "ordinary Admin list hides synthetic mixed correction status");
    Actor(demoAdminController, workflowOwner);
    demoAdminController.Index(null);
    Check(((HashSet<Guid>)demoAdminController.ViewBag.CorrectionBookingIds).Contains(demoMixed.Id),
        "active SuperAdmin list can see an explicitly supplied Demo mixed correction status");
    var view = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"Frontend","GlamByMariya.Web","Views","Admin","_MixedBookingDetails.cshtml"));
    Check(view.Contains("reviewRevision = payment.PersistentReviewRevision") && view.Contains("reviewVersion = payment.PersistentReviewVersion") &&
          view.Contains("name=\"reviewRevision\"") && view.Contains("name=\"reviewVersion\""), "mixed review carries persistent proof revision and row version (source contract)");
    Console.WriteLine($"{checks} mixed read-model preparation checks passed; fake snapshots only.");
    return;
}
if (args.Contains("--persistent-mixed-guard-only"))
{
    var persistent = System.Reflection.DispatchProxy.Create<IPersistentStudioStore, PersistentQuoteProxy>();
    var proxy = (PersistentQuoteProxy)persistent;
    var flow = new BookingService(persistent, persistent, persistent, persistent, persistent);
    void Unavailable(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException e) when (e.Message == "Mixed-service bookings are not available in persistent storage.")
        { Check(true, label); return; }
        throw new Exception("Expected precise persistent mixed guard: " + label);
    }
    var people = new[] { new MixedPersonSelection(service.Id, []), new MixedPersonSelection(service.Id, []) };
    Unavailable(() => flow.QuoteMixed(people), "persistent mixed quote fails closed");
    Unavailable(() => flow.FindMixedAlternatives(people, DateTime.Today, 1), "persistent mixed alternatives fail closed");
    Unavailable(() => flow.CreateMixed(customer.Id, people, DateTime.Today.AddDays(10), "Synthetic address", "Quezon City", 500,
        "GCash", "SYNTHETIC", "proof", true, true, 1000, 500), "persistent mixed creation fails closed");
    Check(proxy.Arguments == null, "no persistent booking command ran");
    var controller = new BookingsController(persistent, flow, null!, null!, null!, null!);
    Check(controller.MixedSelection() is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed selection route fails closed");
    Check(controller.MixedQuote(people) is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed quote route fails closed");
    Check(controller.MixedAvailability(null!) is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed availability route fails closed");
    Check(controller.MixedAlternatives(null!) is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed alternatives route fails closed");
    Check(controller.MixedValidateSlot(null!) is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed slot route fails closed");
    var submit = controller.CreateMixed(default, "", "", 0, "", "", null, false, false, null, null, null, Guid.Empty, null, null).GetAwaiter().GetResult();
    Check(submit is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed submission route fails closed");
    Console.WriteLine($"{checks} persistent mixed guard checks passed; no PostgreSQL was opened.");
    return;
}
if (args.Contains("--persistent-review-only"))
{
    var id = Guid.NewGuid();
    var payment = new Payment { Id = id, ProofKey = "Proofs/synthetic.png", PersistentReviewRevision = 3, PersistentReviewVersion = "42" };
    BookingService.ValidatePersistentPaymentReview(payment, id, 3, "42", false);
    Check(true, "matching unverified claim accepted");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 2, "42", false), "stale revision rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, null, "42", false), "missing revision rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, -1, "42", false), "negative revision rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, Guid.NewGuid(), 3, "42", false), "wrong payment rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 3, "43", false), "changed row version rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 3, "invalid", false), "malformed row version rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 3, null, false), "missing row version rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, Guid.Empty, 3, "42", false), "empty payment identifier rejected");
    payment.ProofKey = null;
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 3, "42", false), "missing proof rejected");
    payment.ProofKey = "Proofs/synthetic.png";
    payment.Verified = true; payment.PersistentReviewRevision = 4;
    BookingService.ValidatePersistentPaymentReview(payment, id, 3, "42", true);
    Check(true, "exact already-committed verification retry accepted without another mutation");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 2, "42", true), "older confirmed review rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, long.MaxValue, "42", true), "overflowing revision rejected");
    var gate = new object(); var changed = new Payment { Id = id, ProofKey = "Proofs/new.png", PersistentReviewRevision = 0, PersistentReviewVersion = "42" };
    var verifiedWrongProof = false;
    for (var run = 0; run < 100; run++)
    {
        changed.PersistentReviewRevision = 0; changed.Verified = false;
        using var barrier = new Barrier(2);
        var correct = Task.Run(() => { barrier.SignalAndWait(); lock (gate) { if (!changed.Verified) changed.PersistentReviewRevision++; } });
        var verify = Task.Run(() => { barrier.SignalAndWait(); lock (gate) { try { BookingService.ValidatePersistentPaymentReview(changed, id, 0, "42", false); changed.Verified = true; } catch (InvalidOperationException) { } if (changed.Verified && changed.PersistentReviewRevision != 0) verifiedWrongProof = true; } });
        Task.WaitAll(correct, verify);
    }
    Check(!verifiedWrongProof, "fake serialized correction and verification never verify a changed claim");
    var baseSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Data", "PostgresStudioStore.cs"));
    var publicSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Data", "PublicStudioStore.cs"));
    var controllerSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Controllers", "AdminController.cs"));
    Check(baseSource.Contains("Admin(admin); var b=LockBooking(booking);") && baseSource.Contains("FOR UPDATE OF p") && baseSource.Contains("FOR UPDATE\""), "makeup and public payment rows are locked after staff and booking checks (source contract)");
    Check(publicSource.Contains("Admin(admin);") && publicSource.Contains("CheckPaymentReview(booking, paymentId, reviewRevision"), "public review checks run after staff authorization (source contract)");
    Check(controllerSource.Contains("[HttpPost, ValidateAntiForgeryToken]") && controllerSource.Contains("if (!workflow.CanManageBooking(b, UserId)) return Forbid();"), "controller retains antiforgery and actor checks (source contract)");
    Check(controllerSource.Contains("current.PersistentReviewVersion != reviewVersion") && controllerSource.Contains("current?.PersistentReviewRevision != reviewRevision"), "proof retrieval requires the displayed claim version (source contract)");
    Console.WriteLine($"{checks} persistent payment-review checks passed; fake concurrency and source contracts only.");
    return;
}
if (args.Contains("--persistent-quote-only"))
{
    var quote = workflow.Quote(service.Id, []);
    var persistent = System.Reflection.DispatchProxy.Create<IPersistentStudioStore, PersistentQuoteProxy>();
    var proxy = (PersistentQuoteProxy)persistent;
    var persistentFlow = new BookingService(persistent, persistent, persistent, persistent, persistent);
    Reject(() => persistentFlow.Create(customer.Id, service.Id, [], DateTime.Today.AddDays(1), "Test address", "Quezon City", 1, "GCash", "REF", null, true, travelAcknowledged: true), "persistent submission requires a reviewed quote");
    Check(proxy.Arguments == null, "missing quote never reaches persistent repository");
    persistentFlow.Create(customer.Id, service.Id, [], DateTime.Today.AddDays(1), "Test address", "Quezon City", 1, "GCash", "REF", null, true, travelAcknowledged: true, expectedTotal: quote.FinalTotal, expectedMinimum: quote.RequiredDownPayment);
    Check(proxy.Arguments != null && (decimal?)proxy.Arguments[14] == quote.FinalTotal && (decimal?)proxy.Arguments[15] == quote.RequiredDownPayment, "persistent workflow forwards both reviewed amounts to repository");
    BookingService.ValidateExpectedQuote(quote, quote.FinalTotal, quote.RequiredDownPayment);
    Check(true, "matching reviewed total and minimum accepted");
    Reject(() => BookingService.ValidateExpectedQuote(quote, quote.FinalTotal + 1, quote.RequiredDownPayment), "changed total rejected even with matching minimum");
    Reject(() => BookingService.ValidateExpectedQuote(quote, quote.FinalTotal, quote.RequiredDownPayment + 1), "changed minimum rejected");
    Reject(() => BookingService.ValidateExpectedQuote(quote, quote.FinalTotal, null), "partial reviewed quote rejected");
    Reject(() => BookingService.ValidateExpectedQuote(quote, null, quote.RequiredDownPayment), "missing total rejected");
    BookingService.ValidateExpectedQuote(quote, null, null);
    Check(true, "legacy direct repository calls remain compatible");
    var root = Directory.GetCurrentDirectory();
    foreach (var name in new[] { "PostgresStudioStore.cs", "PublicStudioStore.cs" })
    {
        var source = File.ReadAllText(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Data", name));
        var begin = source.IndexOf("SubmitBooking(", StringComparison.Ordinal);
        var validation = source.IndexOf("BookingService.ValidateExpectedQuote(quote, expectedTotal, expectedMinimum)", begin, StringComparison.Ordinal);
        var insert = source.IndexOf("INSERT INTO ", begin, StringComparison.Ordinal);
        Check(begin >= 0 && validation > begin && insert > validation, $"{name}: quote checked before booking insert (source contract)");
    }
    Console.WriteLine($"{checks} persistent quote checks passed; no PostgreSQL was opened.");
    return;
}
if (args.Contains("--mixed-retry-only"))
{
    foreach (var scenario in new[] { "service", "schedule", "addons", "quantity", "person order", "address", "method", "reference", "proof", "agreement", "travel consent", "policy", "travel version", "amount", "expected total", "expected minimum" })
    {
        var demo = new DemoStore(); var flow = new BookingService(demo, demo, demo, demo, demo);
        var actor = demo.Users.First(x => x.Role == Roles.Customer); var services = demo.Services.Take(2).ToArray(); var addons = demo.AddOns.Take(2).ToArray();
        var start = DateTime.Today.AddDays(27).AddHours(8); var id = Guid.NewGuid();
        var people = new[] { new MixedPersonSelection(services[0].Id, [addons[0].Id, addons[1].Id]), new MixedPersonSelection(services[1].Id, []) };
        var quote = flow.QuoteMixed(people, start); var policy = demo.Settings.PolicyVersion; var travel = demo.Settings.TravelReminderVersion;
        Booking Submit(MixedPersonSelection[] selected, DateTime time, string address, string area, decimal amount, string method, string reference, string proof, bool agreed, bool acknowledged, decimal total, decimal minimum, string version, string travelVersion, Guid? customerId = null) =>
            flow.CreateMixed(customerId ?? actor.Id, selected, time, address, area, amount, method, reference, proof, agreed, acknowledged, total, minimum, id, version, travelVersion);
        var initialMixed = Submit(people, start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "Ref-A", "proof-A", true, true, quote.FinalTotal, quote.RequiredDownPayment, policy, travel);
        var payment = demo.Payments.Single(x => x.BookingId == initialMixed.Id);
        var bookingBefore = System.Text.Json.JsonSerializer.Serialize(initialMixed); var paymentBefore = System.Text.Json.JsonSerializer.Serialize(payment);
        var bookingCount = demo.Bookings.Count; var retryPaymentCount = demo.Payments.Count;
        var changedPeople = scenario switch {
            "service" => new[] { new MixedPersonSelection(services[1].Id, [addons[0].Id, addons[1].Id]), people[1] },
            "addons" => new[] { new MixedPersonSelection(services[0].Id, [addons[0].Id]), people[1] },
            "quantity" => new[] { new MixedPersonSelection(services[0].Id, [addons[0].Id, addons[0].Id, addons[1].Id]), people[1] },
            "person order" => new[] { people[1], people[0] }, _ => people
        };
        try
        {
            Submit(changedPeople, scenario == "schedule" ? start.AddDays(1) : start, scenario == "address" ? "Different address" : "Synthetic address",
                "Quezon City", scenario == "amount" ? quote.RequiredDownPayment + 1 : quote.RequiredDownPayment,
                scenario == "method" ? "Maya" : "GCash", scenario == "reference" ? "Ref-B" : "Ref-A",
                scenario == "proof" ? "proof-B" : "proof-A", scenario != "agreement", scenario != "travel consent",
                scenario == "expected total" ? quote.FinalTotal + 1 : quote.FinalTotal,
                scenario == "expected minimum" ? quote.RequiredDownPayment + 1 : quote.RequiredDownPayment,
                scenario == "policy" ? "other" : policy, scenario == "travel version" ? "other" : travel);
            throw new Exception($"Expected changed {scenario} rejection");
        }
        catch (InvalidOperationException e) when (e.Message == "Submission ID was already used for different booking details.") { Check(true, $"changed {scenario} receives precise retry rejection"); }
        Check(bookingBefore == System.Text.Json.JsonSerializer.Serialize(initialMixed) && paymentBefore == System.Text.Json.JsonSerializer.Serialize(payment)
            && demo.Bookings.Count == bookingCount && demo.Payments.Count == retryPaymentCount, $"changed {scenario} preserves original records and counts");
        var reversedAddons = new[] { new MixedPersonSelection(services[0].Id, [addons[1].Id, addons[0].Id]), people[1] };
        demo.Settings.PolicyVersion = "new-policy"; demo.Settings.TravelReminderVersion = "new-travel"; actor.Name = "Changed profile";
        var retry = Submit(reversedAddons, start, " Synthetic address ", "quezon city", quote.RequiredDownPayment, "GCash", " Ref-A ", " proof-A ", true, true, quote.FinalTotal, quote.RequiredDownPayment, policy, travel);
        Check(retry.Id == initialMixed.Id && demo.Bookings.Count == bookingCount && demo.Payments.Count == retryPaymentCount, $"{scenario}: identical normalized retry survives policy and profile changes");
    }
    {
        var demo = new DemoStore(); var flow = new BookingService(demo, demo, demo, demo, demo);
        var actor = demo.Users.First(x => x.Role == Roles.Customer); var item = demo.Services.First();
        var start = DateTime.Today.AddDays(25).AddHours(8); var people = new[] { new MixedPersonSelection(item.Id, []), new MixedPersonSelection(item.Id, []) };
        var quote = flow.QuoteMixed(people, start); var id = Guid.NewGuid();
        var before = demo.Bookings.Count; var paymentBefore = demo.Payments.Count;
        try { flow.CreateMixed(actor.Id, people, start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "PIXEL", "staged-a", true, true, quote.FinalTotal, quote.RequiredDownPayment, id, "old", demo.Settings.TravelReminderVersion, "PIXELS-A"); throw new Exception("Expected stale policy rejection"); }
        catch (InvalidOperationException e) when (e.Message == "The booking policies changed. Reload and review them before submitting.") { Check(true, "initial stale policy receives precise rejection"); }
        try { flow.CreateMixed(actor.Id, people, start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "PIXEL", "staged-a", true, true, quote.FinalTotal, quote.RequiredDownPayment, id, demo.Settings.PolicyVersion, "old", "PIXELS-A"); throw new Exception("Expected stale travel policy rejection"); }
        catch (InvalidOperationException e) when (e.Message == "The booking policies changed. Reload and review them before submitting.") { Check(true, "initial stale travel version receives precise rejection"); }
        Check(demo.Bookings.Count == before && demo.Payments.Count == paymentBefore, "stale policy creates no records");
        Booking SubmitImage(string path, string fingerprint) => flow.CreateMixed(actor.Id, people, start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "PIXEL", path, true, true, quote.FinalTotal, quote.RequiredDownPayment, id, demo.Settings.PolicyVersion, demo.Settings.TravelReminderVersion, fingerprint);
        var initialImage = SubmitImage("staged-a", "PIXELS-A");
        Check(SubmitImage("staged-b", "PIXELS-A").Id == initialImage.Id, "same image fingerprint ignores different staged paths");
        try { SubmitImage("staged-c", "PIXELS-B"); throw new Exception("Expected proof mismatch"); }
        catch (InvalidOperationException e) when (e.Message == "Submission ID was already used for different booking details.") { Check(true, "changed image fingerprint receives precise retry rejection"); }
        Check(demo.Bookings.Count == before + 1 && demo.Payments.Count == paymentBefore + 1 && demo.Payments.Single(x => x.BookingId == initialImage.Id).ProofPath == "staged-a", "image retries preserve initial payment and record counts");
    }
    {
        var demo = new DemoStore(); var flow = new BookingService(demo, demo, demo, demo, demo);
        var actor = demo.Users.First(x => x.Role == Roles.Customer); var item = demo.Services.First();
        var start = DateTime.Today.AddDays(24).AddHours(8);
        var selected = new[] { new MixedPersonSelection(item.Id, []), new MixedPersonSelection(item.Id, []) };
        var quote = flow.QuoteMixed(selected, start); var id = Guid.NewGuid();
        Booking SubmitNullable(IReadOnlyList<MixedPersonSelection>? people, string? area, string? reference) =>
            flow.CreateMixed(actor.Id, people, start, "Synthetic address", area, quote.RequiredDownPayment,
                "GCash", reference, "proof", true, true, quote.FinalTotal, quote.RequiredDownPayment,
                id, demo.Settings.PolicyVersion, demo.Settings.TravelReminderVersion);
        var initialBookings = demo.Bookings.Count; var initialPayments = demo.Payments.Count;
        foreach (var (label, action, expected) in new (string, Action, string)[] {
            ("people", () => SubmitNullable(null, "Quezon City", "Ref-null"), $"Choose between 2 and {BookingPricing.MaximumPeople} people."),
            ("area", () => SubmitNullable(selected, null, "Ref-null"), "The address must be within our service area."),
            ("reference", () => SubmitNullable(selected, "Quezon City", null), "Choose a payment method and provide a valid reference.")
        })
        {
            try { action(); throw new Exception($"Expected null {label} rejection"); }
            catch (InvalidOperationException e) when (e.Message == expected) { Check(true, $"null {label} retains exact initial validation error"); }
            Check(demo.Bookings.Count == initialBookings && demo.Payments.Count == initialPayments, $"null {label} leaves records unchanged");
        }
        var original = SubmitNullable(selected, "Quezon City", "Ref-null");
        var payment = demo.Payments.Single(p => p.BookingId == original.Id);
        var snapshot = System.Text.Json.JsonSerializer.Serialize(original) + System.Text.Json.JsonSerializer.Serialize(payment);
        foreach (var (label, action) in new (string, Action)[] {
            ("people", () => SubmitNullable(null, "Quezon City", "Ref-null")),
            ("area", () => SubmitNullable(selected, null, "Ref-null")),
            ("reference", () => SubmitNullable(selected, "Quezon City", null))
        })
        {
            try { action(); throw new Exception($"Expected null {label} retry rejection"); }
            catch (InvalidOperationException e) when (e.Message == "Submission ID was already used for different booking details.") { Check(true, $"null {label} retry retains exact mismatch error"); }
            Check(snapshot == System.Text.Json.JsonSerializer.Serialize(original) + System.Text.Json.JsonSerializer.Serialize(payment)
                && demo.Bookings.Count == initialBookings + 1 && demo.Payments.Count == initialPayments + 1,
                $"null {label} retry preserves booking and payment");
        }
    }
    Console.WriteLine($"All {checks} isolated mixed retry checks passed."); return;
}
if (args.Contains("--boundary-regression-only"))
{
    // A fresh in-memory fixture per run; never exercises a database or HTTP authorization.
    var demo = new DemoStore(); var flow = new BookingService(demo, demo, demo, demo, demo);
    var boundaryCustomer = demo.Users.First(u => u.Role == Roles.Customer);
    var other = demo.AddUser("Other", "Customer", "other@example.test", "Synthetic123!", Roles.Customer);
    var boundaryAdmin = demo.Users.First(u => u.Role == Roles.Admin);
    var owner = demo.Users.First(u => u.Role == Roles.SuperAdmin);
    var look = demo.Services.First(); var start = DateTime.Today.AddDays(28).AddHours(8);
    var quote = flow.Quote(look.Id, [], 1, "auto", start);
    Booking Submit(DateTime at, string address = "Synthetic service address", decimal? amount = null, string method = "GCash", string reference = "BOUNDARY-1", bool agreed = true, bool travel = true, decimal? total = null) =>
        flow.Create(boundaryCustomer.Id, look.Id, [], at, address, "Quezon City", amount ?? quote.RequiredDownPayment, method, reference, null, agreed, 1, "auto", travel, total ?? quote.FinalTotal, quote.RequiredDownPayment);
    var initialBookings = demo.Bookings.Count; var initialPayments = demo.Payments.Count;
    foreach (var (label, action) in new (string, Action)[] {
        ("short address", () => Submit(start, address: "short")),
        ("overlong address", () => Submit(start, address: new string('x', 501))),
        ("missing agreement", () => Submit(start, agreed: false)),
        ("missing travel acknowledgement", () => Submit(start, travel: false)),
        ("stale quoted total", () => Submit(start, total: quote.FinalTotal + 1)),
        ("payment precision", () => Submit(start, amount: quote.RequiredDownPayment + 0.001m)),
        ("cash deposit", () => Submit(start, method: "Cash")),
        ("blank payment reference", () => Submit(start, reference: " "))
    })
    {
        Reject(action, label + " rejected");
        Check(demo.Bookings.Count == initialBookings && demo.Payments.Count == initialPayments, label + " leaves bookings and payments unchanged");
    }
    var booking = Submit(start); var payment = demo.Payments.Single(p => p.BookingId == booking.Id);
    Check(booking.Status == "Pending Payment" && flow.HoldsReservation(booking) && !payment.Verified, "new pending booking holds slot with unverified payment");
    Reject(() => flow.CheckSlot(start, quote.DurationMinutes), "pending booking blocks its slot");
    var history = booking.History.Count; var originalReference = payment.Reference;
    foreach (var actor in new[] { boundaryCustomer.Id, other.Id, Guid.NewGuid() })
        Reject(() => flow.AdminAction(booking, "verify", actor, "", 0, ""), "unauthorized or unknown actor cannot verify");
    Reject(() => flow.AdminAction(booking, "unknown-action", owner.Id, "", 0, ""), "unknown admin action rejected");
    Reject(() => flow.AdminAction(booking, "reject", owner.Id, " ", 0, ""), "rejection requires reason");
    Check(booking.Status == "Pending Payment" && !payment.Verified && booking.History.Count == history && payment.Reference == originalReference && flow.Paid(booking) == 0, "failed admin actions preserve booking, history and payment");
    var areas = demo.Settings.ServiceAreas;
    demo.Settings.ServiceAreas = "Synthetic other area";
    Reject(() => flow.AdminAction(booking, "verify", owner.Id, "", 0, ""), "verification rechecks service area");
    Check(booking.Status == "Pending Payment" && !payment.Verified && booking.History.Count == history, "failed verification is atomic for booking and payment");
    demo.Settings.ServiceAreas = areas;
    flow.AdminAction(booking, "verify", owner.Id, "", 0, "");
    Check(booking.Status == "Confirmed" && payment.Verified && flow.HoldsReservation(booking), "verified booking remains reserved");
    Reject(() => flow.CheckSlot(start, quote.DurationMinutes), "confirmed booking blocks its slot");
    history = booking.History.Count;
    Reject(() => flow.Cancel(booking, other.Id, true), "another Customer cannot cancel booking");
    Reject(() => flow.Cancel(booking, boundaryCustomer.Id, false), "owner cancellation needs confirmation");
    Reject(() => flow.OfferReschedule(booking, other.Id, start.AddDays(1)), "another Customer cannot request reschedule");
    Reject(() => flow.AdminAction(booking, "cancel", boundaryAdmin.Id, "synthetic", 0, "", true), "ordinary Admin cannot cancel as business");
    Check(booking.Status == "Confirmed" && payment.Verified && booking.History.Count == history && flow.HoldsReservation(booking), "unauthorized attempts preserve confirmed reservation and payment");
    Reject(() => Submit(start.AddDays(1), reference: originalReference), "duplicate payment reference rejected at another slot");
    Check(demo.Bookings.Count == initialBookings + 1 && demo.Payments.Count == initialPayments + 1, "duplicate reference creates no booking or payment");
    flow.Cancel(booking, boundaryCustomer.Id, true, "Synthetic cancellation");
    flow.CheckSlot(start, quote.DurationMinutes);
    Check(booking.Status == "Cancelled" && !flow.HoldsReservation(booking) && payment.Verified, "customer cancellation releases slot and preserves verified payment");
    Reject(() => flow.Cancel(booking, boundaryCustomer.Id, true), "duplicate customer cancellation rejected");
    // This tests the existing in-process mixed-submission behavior only. The
    // current implementation does not compare changed payloads on retry.
    var mixedStart = start.AddDays(3);
    var selection = new[] { new MixedPersonSelection(look.Id, []), new MixedPersonSelection(look.Id, []) };
    var mixedQuote = flow.QuoteMixed(selection, mixedStart);
    var submission = Guid.NewGuid();
    Booking SubmitMixed(Guid actor, DateTime at, string reference) => flow.CreateMixed(actor, selection, at,
        "Synthetic service address", "Quezon City", mixedQuote.RequiredDownPayment, "GCash", reference,
        "synthetic-proof.png", true, true, mixedQuote.FinalTotal, mixedQuote.RequiredDownPayment, submission, demo.Settings.PolicyVersion, demo.Settings.TravelReminderVersion);
    var beforeMixedBookings = demo.Bookings.Count; var beforeMixedPayments = demo.Payments.Count;
    using (var retryBarrier = new Barrier(2))
    {
        var retries = Enumerable.Range(0, 2).Select(_ => Task.Run(() => {
            retryBarrier.SignalAndWait(); return SubmitMixed(boundaryCustomer.Id, mixedStart, "MIXED-BOUNDARY-A");
        })).ToArray();
        Task.WaitAll(retries);
        Check(retries[0].Result.Id == retries[1].Result.Id, "concurrent identical mixed submissions return the same booking");
        Check(demo.Bookings.Count == beforeMixedBookings + 1 && demo.Payments.Count == beforeMixedPayments + 1,
            "concurrent mixed retries create one booking and one payment");
        var otherBooking = SubmitMixed(other.Id, mixedStart.AddDays(1), "MIXED-BOUNDARY-B");
        Check(otherBooking.Id != retries[0].Result.Id && otherBooking.CustomerId == other.Id,
            "same mixed submission ID is scoped to its Customer");
        Check(demo.Bookings.Count == beforeMixedBookings + 2 && demo.Payments.Count == beforeMixedPayments + 2,
            "second Customer submission creates a separate booking and payment");
    }
    Console.WriteLine($"All {checks} isolated fake-backed boundary regression checks passed."); return;
}
if (args.Contains("--staff-cancellation-completion-only"))
{
    var demo = new DemoStore(); var flow = new BookingService(demo, demo, demo, demo, demo);
    var owner = demo.Users.First(u => u.Role == Roles.SuperAdmin); var staff = demo.Users.First(u => u.Role == Roles.Admin); var user = demo.Users.First(u => u.Role == Roles.Customer);
    var initial = demo.Bookings.Single(); var payment = demo.Payments.Single(p => p.BookingId == initial.Id); payment.ProofPath = "synthetic-proof.png";
    var staffPaymentCount = demo.Payments.Count; var reference = payment.Reference;
    Reject(() => flow.AdminAction(initial, "cancel", staff.Id, "synthetic reason", 0, "", true), "ordinary Admin cannot cancel");
    Reject(() => flow.AdminAction(initial, "cancel", user.Id, "synthetic reason", 0, "", true), "Customer cannot cancel as staff");
    Reject(() => flow.AdminAction(initial, "cancel", owner.Id, "synthetic reason", 0, ""), "staff cancellation requires explicit confirmation");
    Reject(() => flow.AdminAction(initial, "cancel", owner.Id, " ", 0, "", true), "staff cancellation requires reason");
    flow.AdminAction(initial, "cancel", owner.Id, "Synthetic business cancellation", 0, "", true);
    Check(initial.Status == "Cancelled" && !flow.HoldsReservation(initial) && initial.StaffCancelledBy == owner.Id && initial.StaffCancelledAt.HasValue && initial.StaffCancellationReason == "Synthetic business cancellation", "SuperAdmin cancellation records actor, timestamp and reason and releases pending reservation");
    Check(!payment.Verified && payment.Reference == reference && payment.ProofPath == "synthetic-proof.png" && demo.Payments.Count == staffPaymentCount && initial.RefundDue == 0 && initial.Refunded == 0, "pending cancellation preserves unverified payment and creates no refund");
    Reject(() => flow.VerifyCancelledReceipt(initial, staff.Id, payment.Id, payment.Revision, true), "ordinary Admin cannot verify cancelled receipt");
    Reject(() => flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision, true), "receipt verification requires available proof");
    var receiptProof = Path.GetTempFileName();
    try {
        payment.ProofPath = receiptProof;
        Reject(() => flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision + 1, true), "stale receipt revision rejected");
        Reject(() => flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision, false), "receipt verification requires review confirmation");
        flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision, true);
        Check(payment.Verified && payment.VerifiedBy == owner.Id && payment.VerifiedAt.HasValue && initial.Status == "Cancelled" && initial.RefundDue == 0 && initial.Refunded == 0, "receipt verification keeps cancellation and refund unchanged");
        Reject(() => flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision, true), "duplicate cancelled receipt verification rejected");
    } finally { File.Delete(receiptProof); }
    Reject(() => flow.AdminAction(initial, "cancel", owner.Id, "again", 0, "", true), "duplicate cancellation rejected");
    Reject(() => flow.AdminAction(initial, "complete", owner.Id, "", 0, "", true), "cancelled booking cannot be completed");
    var item = demo.Services.First(); var day = DateTime.Today.AddDays(30).AddHours(8); var quote = flow.Quote(item.Id, [], 1, "auto", day);
    Booking CreateStaffFixture(int shift) => flow.Create(user.Id, item.Id, [], day.AddDays(shift), "Synthetic service address", "Quezon City", quote.RequiredDownPayment, "GCash", "STAFF-ACTION-" + shift, "proof.png", true, 1, "auto", true, quote.FinalTotal, quote.RequiredDownPayment);
    var confirmedBooking = CreateStaffFixture(0); flow.AdminAction(confirmedBooking, "verify", owner.Id, "", 0, ""); var verifiedPayment = demo.Payments.Single(p => p.BookingId == confirmedBooking.Id); var staffVerifiedAt = verifiedPayment.VerifiedAt;
    Reject(() => flow.AdminAction(confirmedBooking, "complete", owner.Id, "", 0, "", true), "future appointment cannot be completed");
    flow.AdminAction(confirmedBooking, "cancel", owner.Id, "Synthetic confirmed cancellation", 0, "", true);
    Check(verifiedPayment.Verified && verifiedPayment.VerifiedBy == owner.Id && verifiedPayment.VerifiedAt == staffVerifiedAt && confirmedBooking.RefundDue == 0 && confirmedBooking.Refunded == 0, "confirmed cancellation preserves verified claim without inventing refund");
    var complete = CreateStaffFixture(1); flow.AdminAction(complete, "verify", owner.Id, "", 0, ""); var claim = demo.Payments.Single(p => p.BookingId == complete.Id); var before = demo.Payments.Count;
    complete.Start = DateTime.Now.AddDays(-2);
    Reject(() => flow.AdminAction(complete, "complete", staff.Id, "", 0, "", true), "ordinary Admin cannot complete");
    Reject(() => flow.AdminAction(complete, "complete", owner.Id, "", 0, ""), "completion requires explicit confirmation");
    flow.AdminAction(complete, "complete", owner.Id, "", 0, "", true);
    Check(complete.Status == "Completed" && complete.CompletedBy == owner.Id && complete.CompletedAt.HasValue, "manual completion records actor and timestamp");
    Check(demo.Payments.Count == before && claim.Verified && flow.Paid(complete) == claim.Amount && complete.Refunded == 0, "completion does not settle outstanding balance or create a payment or refund");
    Reject(() => flow.AdminAction(complete, "complete", owner.Id, "", 0, "", true), "duplicate completion rejected");
    var rejected = CreateStaffFixture(2); flow.AdminAction(rejected, "reject", owner.Id, "Synthetic rejection", 0, "");
    Reject(() => flow.AdminAction(rejected, "complete", owner.Id, "", 0, "", true), "rejected booking cannot be completed");
    // Deliberately synthetic states: the unpaid confirmed fixture is not claimed
    // to be reachable through normal booking verification or persistent SQL.
    foreach (var scenario in new[] { "unpaid", "partial", "full" })
    {
        var local = new DemoStore(); var testFlow = new BookingService(local, local, local, local, local);
        var client = local.Users.First(u => u.Role == Roles.Customer);
        var testOwner = local.Users.First(u => u.Role == Roles.SuperAdmin);
        var serviceItem = local.Services.First();
        var start = DateTime.Today.AddDays(25).AddHours(8);
        var quoted = testFlow.Quote(serviceItem.Id, [], 1, "auto", start);
        var submitted = scenario == "full" ? quoted.FinalTotal : quoted.RequiredDownPayment;
        var appointment = testFlow.Create(client.Id, serviceItem.Id, [], start, "Synthetic service address", "Quezon City", submitted, "GCash", "FINANCE-" + scenario, "synthetic-proof.png", true, 1, "auto", true, quoted.FinalTotal, quoted.RequiredDownPayment);
        var deposit = local.Payments.Single(p => p.BookingId == appointment.Id);
        Reject(() => testFlow.AdminAction(appointment, "verify", client.Id, "", 0, ""), $"{scenario}: Customer cannot verify payment");
        Check(!deposit.Verified && testFlow.Paid(appointment) == 0, $"{scenario}: unverified claim is not collected");
        if (scenario == "unpaid") appointment.Status = "Confirmed";
        else testFlow.AdminAction(appointment, "verify", testOwner.Id, "", 0, "");
        var originalTotal = appointment.Total; var originalPaid = testFlow.Paid(appointment);
        var originalCount = local.Payments.Count; var originalRef = deposit.Reference; var wasVerified = deposit.Verified;
        appointment.Start = DateTime.Today.AddDays(-2).AddHours(8);
        testFlow.AdminAction(appointment, "complete", testOwner.Id, "", 0, "", true);
        Check(appointment.Status == "Completed" && appointment.Total == originalTotal && testFlow.Paid(appointment) == originalPaid
            && appointment.RemainingBalance == originalTotal - originalPaid,
            $"{scenario}: completion preserves agreed total, verified collections and outstanding balance");
        Check(local.Payments.Count == originalCount && local.Payments.Contains(deposit) && deposit.Reference == originalRef && deposit.Verified == wasVerified,
            $"{scenario}: completion preserves original payment evidence without inserting a payment");
        Reject(() => testFlow.AdminAction(appointment, "complete", testOwner.Id, "", 0, "", true), $"{scenario}: duplicate completion rejected");
        Check(local.Payments.Count == originalCount, $"{scenario}: duplicate completion creates no finance record");
    }
    Check(demo.AdminActivity().Any(a => a.TargetId == initial.Id && a.Action == "Appointment cancelled by SuperAdmin") && demo.AdminActivity().Any(a => a.TargetId == complete.Id && a.Action == "Appointment completed"), "activity contains cancellation and completion");
    Console.WriteLine($"All {checks} staff cancellation and completion service checks passed."); return;
}
if (args.Contains("--reschedule-policy-only"))
{
    var demo = new DemoStore(); var flow = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == Roles.Customer); var owner = demo.Users.First(u => u.Role == Roles.SuperAdmin); var staff = demo.Users.First(u => u.Role == Roles.Admin);
    var day = DateTime.Today.AddDays(30); demo.Settings.EarlyFeeEnabled = true; demo.Settings.EarlyStartHour = 5; demo.Settings.EarlyFee = 200;
    var policyService = demo.Services.First(); var initial = flow.Quote(policyService.Id, [], 1, "auto", day.AddHours(8));
    var booking = flow.Create(user.Id, policyService.Id, [], day.AddHours(8), "Synthetic address", "Quezon City", initial.RequiredDownPayment, "GCash", "RESCHEDULE-POLICY-1", "synthetic.png", true, 1, "auto", true, initial.FinalTotal, initial.RequiredDownPayment);
    flow.AdminAction(booking, "verify", owner.Id, "", 0, ""); var paid = flow.Paid(booking); var policyPaymentCount = demo.Payments.Count;
    var offer = flow.OfferReschedule(booking, user.Id, day.AddDays(1).AddHours(5));
    Check(offer.CurrentTotal == initial.FinalTotal && offer.ProposedTotal == initial.FinalTotal + 200 && booking.Start == day.AddHours(8), "offer shows current and revised total without moving reservation");
    Reject(() => flow.ConsentReschedule(booking, user.Id, offer.Id, false), "explicit consent required");
    Reject(() => flow.ConsentReschedule(booking, staff.Id, offer.Id, true), "another actor cannot consent");
    Reject(() => flow.ConsentReschedule(booking, user.Id, Guid.NewGuid(), true), "forged quote rejected");
    demo.Settings.EarlyFee = 250; Reject(() => flow.ConsentReschedule(booking, user.Id, offer.Id, true), "changed quote requires fresh review"); demo.Settings.EarlyFee = 200;
    flow.ConsentReschedule(booking, user.Id, offer.Id, true);
    Reject(() => flow.AdminAction(booking, "reschedule", staff.Id, "", 0, ""), "ordinary Admin cannot approve");
    demo.Settings.EarlyFee = 250; Reject(() => flow.AdminAction(booking, "reschedule", owner.Id, "", 0, ""), "approval rejects changed fee");
    Check(booking.Start == offer.OriginalStart && booking.Total == initial.FinalTotal && flow.Paid(booking) == paid, "failed approval preserves time, price and payment");
    demo.Settings.EarlyFee = 200; flow.AdminAction(booking, "reschedule", owner.Id, "", 0, "");
    Check(booking.Start == offer.ProposedStart && booking.Total == offer.ProposedTotal && booking.InitialBookingTotal == initial.FinalTotal, "approval creates revised total without mutating original snapshot");
    Check(booking.RescheduleConsents.Count == 1 && booking.ApprovedReschedules.Single().ApprovedBy == owner.Id && demo.Payments.Count == policyPaymentCount && flow.Paid(booking) == paid && booking.RefundDue == 0, "consent and approval retained without automatic payment or refund");
    Reject(() => flow.AdminAction(booking, "reschedule", owner.Id, "", 0, ""), "duplicate approval rejected");
    var second = flow.OfferReschedule(booking, user.Id, day.AddDays(2).AddHours(8));
    Check(second.CurrentTotal == offer.ProposedTotal && second.ProposedTotal == initial.FinalTotal && second.ProposedTotal - second.CurrentTotal == -200, "subsequent quote compares against latest revision and supports decreases");
    flow.ConsentReschedule(booking, user.Id, second.Id, true); flow.AdminAction(booking, "reschedule", owner.Id, "", 0, "");
    Check(booking.ApprovedReschedules.Count == 2 && booking.Total == initial.FinalTotal && booking.Note.Contains("please coordinate directly with Glam by Mariya"), "second revision and manual arrangement reminder preserved");
    Console.WriteLine($"All {checks} rescheduling policy service checks passed."); return;
}
if (args.Contains("--mixed-admin-only"))
{
    var demo = new DemoStore(); var flow = new BookingService(demo, demo, demo, demo, demo);
    var member = demo.Users.Single(u => u.Role == Roles.Customer); var staff = demo.Users.Single(u => u.Role == Roles.Admin); var owner = demo.Users.Single(u => u.Role == Roles.SuperAdmin);
    var look = demo.Services.First(); MixedPersonSelection[] selection = [new(look.Id, []), new(look.Id, []), new(look.Id, [])];
    var date = DateTime.Today.AddDays(30).AddHours(8);
    Booking Make(int day) { var q = flow.QuoteMixed(selection, date.AddDays(day)); return flow.CreateMixed(member.Id, selection, date.AddDays(day), "Synthetic test address", "Quezon City", q.RequiredDownPayment, "GCash", Guid.NewGuid().ToString(), "synthetic-proof.png", true, true, q.FinalTotal, q.RequiredDownPayment, policyVersion: demo.Settings.PolicyVersion, travelReminderVersion: demo.Settings.TravelReminderVersion); }
    var managedBooking = Make(0); var payment = demo.Payments.Single(p => p.BookingId == managedBooking.Id); var total = managedBooking.Total; var duration = managedBooking.End - managedBooking.Start;
    Check(!flow.CanManageBooking(managedBooking, staff.Id) && flow.CanManageBooking(managedBooking, owner.Id), "mixed management fails closed for ordinary Admin; SuperAdmin permitted");
    Reject(() => flow.AdminAction(managedBooking, "verify", staff.Id, "", 0, ""), "ordinary Admin cannot verify mixed claims");
    Reject(() => flow.AdminAction(managedBooking, "verify", member.Id, "", 0, ""), "Customer cannot verify claims");
    Reject(() => flow.AdminAction(managedBooking, "verify", Guid.NewGuid(), "", 0, ""), "missing actor is denied");
    owner.IsActive = false; Reject(() => flow.AdminAction(managedBooking, "verify", owner.Id, "", 0, ""), "inactive SuperAdmin is denied"); owner.IsActive = true;
    Reject(() => flow.AdminAction(managedBooking, "approve", owner.Id, "", 0, ""), "unverified payment cannot be approved");
    flow.AdminAction(managedBooking, "verify", owner.Id, "", 0, "");
    Check(payment.Verified && payment.VerifiedBy == owner.Id && payment.VerifiedAt.HasValue && managedBooking.Status == "Confirmed", "verification records reviewer and time and approves atomically");
    Reject(() => flow.AdminAction(managedBooking, "verify", owner.Id, "", 0, ""), "duplicate mixed verification is explicitly rejected");
    flow.AdminAction(managedBooking, "approve", owner.Id, "", 0, ""); Check(managedBooking.History.Count(h => h.Action == "verify") == 1, "approval retry creates no extra payment or decision");
    Reject(() => flow.AdminAction(managedBooking, "reject", owner.Id, "Too late", 0, ""), "confirmed booking cannot be rejected as pending");
    Reject(() => flow.AdminAction(managedBooking, "cancel", owner.Id, "Staff cancellation", 0, ""), "staff cancellation without confirmation is blocked");
    Check(managedBooking.Status == "Confirmed" && flow.HoldsReservation(managedBooking), "unconfirmed staff cancellation retains reservation");
    var rejected = Make(1); var rejectedClaim = demo.Payments.Single(p => p.BookingId == rejected.Id);
    Reject(() => flow.AdminAction(rejected, "reject", owner.Id, " ", 0, ""), "rejection needs a reason");
    flow.AdminAction(rejected, "proof", owner.Id, "Please send a clearer screenshot", 0, "");
    flow.CorrectProof(rejected, member.Id, "REPLACEMENT", "new-synthetic-proof.png");
    Check(rejectedClaim.Revision == 1 && rejectedClaim.ProofHistory.Count == 1 && !rejectedClaim.Verified, "proof correction preserves original reference and proof history and stays unverified");
    flow.AdminAction(rejected, "reject", owner.Id, "Synthetic rejection", 0, "");
    Check(!flow.HoldsReservation(rejected) && rejected.RefundDue == 0 && demo.Payments.Contains(rejectedClaim), "rejection releases reservation but preserves unverified payment history");
    Check(rejected.History.Any(h => h.Reason == "Synthetic rejection") && demo.AdminActivity().Any(a => a.TargetId == rejected.Id && a.Action.Contains("rejected")), "rejection reason and Admin activity retained");
    var old = managedBooking.Start; var target = date.AddDays(3);
    demo.Settings.EarlyFeeEnabled = true; demo.Settings.EarlyStartHour = 5; demo.Settings.EarlyFee = 125;
    var feeOffer = flow.OfferReschedule(managedBooking, member.Id, target.Date.AddHours(5));
    Check(feeOffer.ProposedTotal == total + 125 && managedBooking.Start == old && managedBooking.Total == total, "fee-changing offer preserves original reservation and price until approval");
    demo.Settings.EarlyFeeEnabled = false;
    void Request(Booking item, DateTime proposed) { var quote = flow.OfferReschedule(item, member.Id, proposed); flow.ConsentReschedule(item, member.Id, quote.Id, true); }
    Request(managedBooking, target);
    Check(managedBooking.Start == old && managedBooking.RequestedStart == target, "reschedule proposal retains old reservation");
    Reject(() => flow.OfferReschedule(managedBooking, staff.Id, target), "reschedule ownership enforced");
    Reject(() => flow.OfferReschedule(managedBooking, member.Id, target.AddMinutes(7)), "off-grid replacement rejected");
    var competitor = Make(3);
    Reject(() => flow.AdminAction(managedBooking, "reschedule", owner.Id, "", 0, ""), "fresh competing pending reservation blocks approval");
    Check(managedBooking.Start == old && managedBooking.RequestedStart == target, "failed reschedule keeps original and proposed times");
    flow.AdminAction(competitor, "reject", owner.Id, "Synthetic release", 0, "");
    flow.AdminAction(managedBooking, "reschedule", owner.Id, "", 0, "");
    Check(managedBooking.Start == target && managedBooking.RequestedStart == null && managedBooking.Total == total && managedBooking.End - managedBooking.Start == duration, "approved replacement keeps immutable services, totals and duration");
    var other = Make(4); flow.AdminAction(other, "verify", owner.Id, "", 0, "");
    var raceTarget = date.AddDays(6); Request(managedBooking, raceTarget); Request(other, raceTarget);
    using (var barrier = new Barrier(2)) {
        var attempts = new[] { managedBooking, other }.Select(x => Task.Run(() => { barrier.SignalAndWait(); try { flow.AdminAction(x, "reschedule", owner.Id, "", 0, ""); return true; } catch (InvalidOperationException) { return false; } })).ToArray(); Task.WaitAll(attempts);
        Check(attempts.Count(t => t.Result) == 1 && new[] { managedBooking, other }.Count(x => x.Start == raceTarget) == 1, "concurrent replacements have exactly one winner");
    }
    Reject(() => flow.Cancel(managedBooking, member.Id, true), "mixed cancellation needs a reason");
    flow.Cancel(managedBooking, member.Id, true, "Synthetic Customer cancellation");
    Check(!flow.HoldsReservation(managedBooking) && managedBooking.CancellationFee == 200 && managedBooking.RefundDue == Math.Max(0, payment.Amount - 200), "Customer cancellation releases occupancy using unchanged verified-payment policy");
    Check(managedBooking.Note == "Synthetic Customer cancellation" && managedBooking.History.Any(h => h.Action == "Customer cancellation"), "Customer cancellation reason and history preserved");
    Reject(() => flow.Cancel(managedBooking, member.Id, true, "Again"), "repeated cancellation is rejected");
    look.Price += 100; look.Minutes += 10; Check(managedBooking.Total == total && managedBooking.End - managedBooking.Start == duration, "historical prices and duration do not follow catalog edits");
    var fakeUser = demo.AddUser("Second", "Customer", "second@example.test", "Synthetic123!", Roles.Customer);
    var http = new Microsoft.AspNetCore.Http.DefaultHttpContext(); http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, fakeUser.Id.ToString()), new(System.Security.Claims.ClaimTypes.Role, Roles.Customer)], "test"));
    var controller = new GlamByMariya.Web.Controllers.BookingsController(demo, flow, null!, null!, null!, null!) { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http } };
    Check(controller.Proof(payment.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult, "another Customer cannot read the first Customer's proof");
    Check(controller.Details(managedBooking.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult, "another Customer cannot inspect the first Customer's booking");
    Check(demo.AdminActivity().Any(a => a.Action == "Reschedule approved") && demo.AdminActivity().Any(a => a.Action.Contains("Payment verified")), "supported staff decisions appear in existing Activity Logs");
    Console.WriteLine($"All {checks} mixed Admin service/security checks passed."); return;
}
if (args.Contains("--mixed-pricing-only"))
{
    var mixedStore = new DemoStore(); var mixedFlow = new BookingService(mixedStore, mixedStore, mixedStore, mixedStore, mixedStore);
    var softMixed = mixedStore.Services.Single(s => s.Name == "Soft Glam");
    var glamourMixed = mixedStore.Services.Single(s => s.Name == "Glamour Makeup");
    var bridalMixed = mixedStore.Services.Single(s => s.Name == "Bridal Makeup");
    var lashMixed = mixedStore.AddOns.Single(s => s.Name == "Lash application");
    var hairMixed = mixedStore.AddOns.Single(s => s.Name == "Hair styling");
    MixedPersonSelection[] threeMixed = [new(bridalMixed.Id, [lashMixed.Id]), new(glamourMixed.Id, [hairMixed.Id]), new(softMixed.Id, [])];
    var mixed = mixedFlow.QuoteMixed(threeMixed);
    Check(mixed.People.Count == 3 && mixed.ServiceTotal == 2850 && mixed.AddOnTotal == 1050 && mixed.FinalTotal == 3900, "three different services use individual catalog prices and add-ons");
    Check(mixed.GroupDiscount == 0 && mixed.DurationMinutes == 330 && mixed.RequiredDownPayment == 1950, "mixed group has no unapproved discount and sums sequential duration");
    var pair = mixedFlow.QuoteMixed(threeMixed.Take(2).ToArray());
    Check(pair.FinalTotal == 3300 && pair.DurationMinutes == 270, "two-person mixed pricing and duration");
    var fiveMixed = mixedFlow.QuoteMixed(Enumerable.Range(0, 5).Select(i => threeMixed[i % 3]).ToArray());
    Check(fiveMixed.People.Count == 5 && fiveMixed.FinalTotal == 7200 && fiveMixed.DurationMinutes == 600, "five-person mixed totals and duration");
    var tenMixed = mixedFlow.QuoteMixed(Enumerable.Repeat(new MixedPersonSelection(bridalMixed.Id, [hairMixed.Id]), 10).ToArray());
    Check(tenMixed.People.Count == 10 && tenMixed.FinalTotal == 23000 && tenMixed.DurationMinutes == 1650, "ten-person selection retains full duration without inventing availability");
    Reject(() => mixedFlow.QuoteMixed([new(Guid.Empty, []), threeMixed[1]]), "missing mixed service rejected");
    Reject(() => mixedFlow.QuoteMixed([new(Guid.NewGuid(), []), threeMixed[1]]), "unknown mixed service rejected");
    Reject(() => mixedFlow.QuoteMixed([new(softMixed.Id, [lashMixed.Id, lashMixed.Id]), threeMixed[1]]), "duplicate per-person add-on rejected");
    Reject(() => mixedFlow.QuoteMixed([threeMixed[0]]), "one-person mixed group rejected");
    mixedStore.Settings.LateFeeEnabled = true; mixedStore.Settings.LateStartHour = 17; mixedStore.Settings.LateEndHour = 23; mixedStore.Settings.LateFee = 234;
    mixedStore.Settings.EarlyFeeEnabled = true; mixedStore.Settings.EarlyStartHour = 5; mixedStore.Settings.EarlyFee = 123;
    var dateMixed = DateTime.Today.AddDays(30);
    var lateMixed = mixedFlow.QuoteMixed(threeMixed, dateMixed.AddHours(13));
    Check(lateMixed.LateFee == 234 && lateMixed.EarlyFee == 0 && lateMixed.FinalTotal == 4134 && lateMixed.RequiredDownPayment == 2067, "one late fee is included in a mixed quote and minimum");
    var earlyMixed = mixedFlow.QuoteMixed(Enumerable.Repeat(new MixedPersonSelection(bridalMixed.Id, []), 7).ToArray(), dateMixed.AddHours(5));
    Check(earlyMixed.EarlyFee == 123 && earlyMixed.LateFee == 0, "early fee takes priority for a long mixed selection");
    mixedStore.Settings.EarlyFeeEnabled = false; mixedStore.Settings.LateFeeEnabled = false; mixedStore.Settings.CloseHour = 20;
    var busyMixed = DateTime.Today.AddDays(40);
    mixedStore.Bookings.Add(new Booking { Start = busyMixed.AddHours(8), Service = new PriceSnapshot(Guid.NewGuid(), "Synthetic reserved service", 100, 300), People = 1, Status = "Confirmed", BufferMinutesSnapshot = 45 });
    mixedStore.Blocks.Add(new AvailabilityBlock(Guid.NewGuid(), busyMixed.AddDays(1).AddHours(8), busyMixed.AddDays(1).AddHours(20), "Synthetic block"));
    MixedPersonSelection[] tenHourMixed = Enumerable.Repeat(new MixedPersonSelection(bridalMixed.Id, []), 5).ToArray();
    var busyCalendar = mixedFlow.GetAvailability(busyMixed, mixedFlow.QuoteMixed(tenHourMixed).DurationMinutes);
    Check(busyCalendar.Days.Single(d => d.Date == busyMixed.ToString("yyyy-MM-dd")).AvailableCount == 0, "ten-hour group cannot fit around a five-hour reserved appointment");
    var shortCalendar = mixedFlow.GetAvailability(busyMixed, mixedFlow.QuoteMixed([new(softMixed.Id, []), new(softMixed.Id, [])]).DurationMinutes);
    Check(shortCalendar.Days.Single(d => d.Date == busyMixed.ToString("yyyy-MM-dd")).AvailableCount > 0, "a reservation does not automatically make the entire day unavailable");
    var suggestions = mixedFlow.FindMixedAlternatives(tenHourMixed, busyMixed, 10);
    Check(suggestions.Dates.Count == 3 && suggestions.Dates[0].Date == busyMixed.AddDays(2).ToString("yyyy-MM-dd") && suggestions.Dates.All(d => d.Slots.Any(s => s.Available)), "suggestions skip the blocked date and contain server-checked start times");
    var none = mixedFlow.FindMixedAlternatives(tenHourMixed, busyMixed, 1);
    Check(none.Dates.Count == 0 && none.DaysChecked == 1 && none.CheckedThrough == busyMixed.AddDays(1), "no results report the checked range without inventing dates");
    Reject(() => mixedFlow.FindMixedAlternatives(tenMixed.People.Select(p => new MixedPersonSelection(p.Service.ItemId, p.AddOns.Select(a => a.ItemId).ToArray())).ToArray(), busyMixed, 3), "overlong selections cannot produce suggestions");
    var bookingDay = busyMixed.AddDays(12).AddHours(8);
    var claim = mixedFlow.QuoteMixed(threeMixed, bookingDay);
    var mixedCustomer = mixedStore.Users.First(u => u.Role == Roles.Customer);
    Reject(() => mixedFlow.CreateMixed(mixedStore.Users.First(u => u.Role == Roles.Admin).Id, threeMixed, bookingDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-ADMIN", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "mixed booking rejects a non-customer actor");
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, bookingDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment - 1, "GCash", "MIXED-LOW", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "mixed booking rejects payment below minimum");
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, bookingDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-NO-AGREEMENT", "synthetic-proof.png", false, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "mixed booking rejects missing agreement");
    using (var mixedBarrier = new Barrier(2))
    {
        var attemptsMixed = Enumerable.Range(0, 2).Select(i => Task.Run(() => { mixedBarrier.SignalAndWait(); try { return mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, bookingDay.AddMinutes(i * 15), "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-CLAIM-" + i, "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion); } catch (InvalidOperationException) { return null; } })).ToArray();
        Task.WaitAll(attemptsMixed);
        var winner = attemptsMixed.Select(x => x.Result).OfType<Booking>().Single();
        Check(winner.MixedPeople.Count == 3 && winner.Total == claim.FinalTotal && (winner.End - winner.Start).TotalMinutes == claim.DurationMinutes, "mixed reservation preserves each service, total and duration");
        winner.AddOns.Clear();
        Check(winner.Total == claim.FinalTotal && winner.MixedPeople[0].AddOns.Count == 1, "mixed total remains derived from per-person snapshots if legacy add-on list changes");
        Check(mixedStore.Payments.Single(p => p.BookingId == winner.Id).Verified == false && mixedFlow.Paid(winner) == 0, "mixed payment is an unverified claim");
        Check(attemptsMixed.Count(x => x.Result != null) == 1, "concurrent mixed reservations have one winner");
        mixedFlow.AdminAction(winner, "verify", mixedStore.Users.First(u => u.Role == Roles.SuperAdmin).Id, "", 0, "");
        Check(winner.Status == "Confirmed" && mixedFlow.Paid(winner) == claim.RequiredDownPayment, "authorized verification accepts mixed booking totals and duration");
    }
    foreach (var (amount, index) in new[] { (claim.RequiredDownPayment + 50, 1), (claim.FinalTotal, 2) })
    {
        var paymentStart = bookingDay.AddDays(index);
        var paymentQuote = mixedFlow.QuoteMixed(threeMixed, paymentStart);
        var paidMixed = mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, paymentStart, "Synthetic address", "Quezon City", amount, "GCash", "MIXED-AMOUNT-" + index, "synthetic-proof.png", true, true, paymentQuote.FinalTotal, paymentQuote.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion);
        Check(mixedStore.Payments.Single(p => p.BookingId == paidMixed.Id).Amount == amount && mixedFlow.Paid(paidMixed) == 0, index == 1 ? "custom mixed payment remains an unverified claim" : "full mixed payment remains an unverified claim");
    }
    var replayKey = Guid.NewGuid();
    var replayDay = bookingDay.AddDays(5);
    var replayBooking = mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-REPLAY", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, replayKey, mixedStore.Settings.PolicyVersion, mixedStore.Settings.TravelReminderVersion);
    var replayBookingAgain = mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-REPLAY", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, replayKey, mixedStore.Settings.PolicyVersion, mixedStore.Settings.TravelReminderVersion);
    Check(replayBooking.Id == replayBookingAgain.Id && mixedStore.Payments.Count(p => p.BookingId == replayBooking.Id) == 1, "idempotent mixed replay returns one booking and payment");
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay.AddDays(1), "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-NO-PROOF", null, true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "mixed service requires a stored proof");
    bridalMixed.Archived = true;
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay.AddDays(1), "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-ARCHIVED", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "submission rechecks current catalog activation");
    bridalMixed.Archived = false;
    bridalMixed.Price = 1800; bridalMixed.Minutes = 150; lashMixed.Price = 400;
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay.AddDays(1), "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-PRICE-CHANGED", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "submission refuses an obsolete catalog quote");
    Check(replayBooking.Total == claim.FinalTotal && replayBooking.MixedSubmission!.ClaimedAmount == claim.RequiredDownPayment && replayBooking.MixedSubmission.Total == claim.FinalTotal && replayBooking.MixedSubmission.BufferMinutes == 45, "booking and immutable submission snapshot survive catalog changes");
    Check(mixed.People[0].Service.Price == 1500 && mixed.People[0].Service.Minutes == 120 && mixed.People[0].AddOns[0].Price == 250 && mixed.FinalTotal == 3900, "mixed quote snapshots survive later catalog changes");
    Console.WriteLine($"All {checks} mixed pricing and reservation checks passed; persistent mixed bookings remain unavailable."); return;
}
if (args.Contains("--late-fees-only"))
{
    var demo = new DemoStore(); var flow = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == Roles.Customer); var reviewer = demo.Users.First(u => u.Role == Roles.Admin);
    var day = DateTime.Today.AddDays(20); var item = demo.Services[0]; var extra = demo.AddOns[0];
    demo.Settings.BufferMinutes = 15;
    Check(flow.Quote(item.Id, [], 1, "auto", day.AddHours(19)).LateFee == 0, "late fee disabled by default");
    demo.Settings.LateFeeEnabled = true; demo.Settings.LateFee = 275; demo.Settings.LateStartHour = 17; demo.Settings.LateEndHour = 22;
    foreach (var look in demo.Services.Where(s => s.Active && !s.Archived))
        Check(flow.Quote(look.Id, [], 1, "auto", day.AddHours(19)).LateFee == 275, $"late fee applies once to {look.Name}");
    var normal = flow.Quote(item.Id, [], 1, "auto", day.AddHours(15));
    var before = flow.Quote(item.Id, [extra.Id], 1, "auto", day.AddHours(17).AddMinutes(-1));
    var exact = flow.Quote(item.Id, [], 1, "auto", day.AddHours(17));
    var late = flow.Quote(item.Id, [], 1, "auto", day.AddHours(19));
    Check(normal.LateFee == 0 && before.LateFee == 0 && exact.LateFee == 275, "individual late fee includes the exact configured start boundary");
    Check(late.FinalTotal == normal.FinalTotal + 275 && late.RequiredDownPayment == Money.Percentage(late.FinalTotal, demo.Settings.DownPaymentPercent), "late fee charged once in total and down payment");
    var group = flow.Quote(item.Id, [extra.Id, extra.Id], 4, "group", day.AddHours(17));
    Check(group.LateFee == 275 && group.DurationMinutes == item.Minutes * 4 + extra.Minutes * 2, "group duration includes add-on quantities and fee applies once");
    var endsAtClose = flow.Quote(item.Id, [], 4, "group", day.AddHours(14));
    var crossesClose = flow.Quote(item.Id, [], 4, "group", day.AddHours(14).AddMinutes(1));
    Check(endsAtClose.LateFee == 0 && crossesClose.LateFee == 275, "package makeup ending exactly at closing is not late; ending after closing is late");
    Check(crossesClose.FinalTotal == endsAtClose.FinalTotal + 275 && crossesClose.RequiredDownPayment == Money.Percentage(crossesClose.FinalTotal, demo.Settings.DownPaymentPercent), "duration-based late fee applies once to quote and down payment");
    flow.CheckSlot(day.AddHours(14), endsAtClose.DurationMinutes);
    Check(endsAtClose.LateFee == 0, "a buffer extending past regular closing alone does not incur the late fee");
    var groupStart = day.AddDays(2).AddHours(14).AddMinutes(1);
    var lateGroupBooking = flow.Create(user.Id, item.Id, [], groupStart, "Synthetic address", "Quezon City", crossesClose.RequiredDownPayment, "GCash", "LATE-GROUP-SNAPSHOT", null, true, 4, "group", true, crossesClose.FinalTotal, crossesClose.RequiredDownPayment);
    Check(lateGroupBooking.LateFeeSnapshot == 275 && lateGroupBooking.Total == crossesClose.FinalTotal, "duration-based fee is preserved in the booking snapshot");
    flow.AdminAction(lateGroupBooking, "verify", reviewer.Id, "", 0, "");
    Reject(() => flow.Reschedule(lateGroupBooking, user.Id, day.AddDays(3).AddHours(13)), "rescheduling cannot silently remove a duration-based late fee");
    flow.CheckSlot(day.AddHours(20).AddMinutes(45), 60);
    Reject(() => flow.CheckSlot(day.AddHours(21), 60), "buffer may not exceed final closing");
    Reject(() => flow.CheckSlot(day.AddHours(21), 120), "9 PM two-hour service is rejected by 10 PM closing");
    Reject(() => flow.CheckSlot(day.AddHours(22), 60), "final closing is not a permitted start");
    Reject(() => flow.CheckSlot(day.AddHours(18), group.DurationMinutes), "group and add-on duration must fit with buffer");
    var slots = flow.GetAvailability(day, 60).Days.Single(d => d.Date == day.ToString("yyyy-MM-dd")).Slots;
    Check(slots.Any(s => s.Start == day.AddHours(20).AddMinutes(45).ToString("yyyy-MM-ddTHH:mm") && s.Available) && !slots.Any(s => s.Start == day.AddHours(21).ToString("yyyy-MM-ddTHH:mm")), "calendar omits starts that cannot finish with buffer");
    demo.Settings.EarlyFeeEnabled = true; demo.Settings.EarlyStartHour = 5; demo.Settings.EarlyFee = 100;
    var early = flow.Quote(item.Id, [], 1, "auto", day.AddHours(5));
    Check(early.EarlyFee == 100 && early.LateFee == 0, "early start is charged early only");
    var earlyLong = flow.Quote(item.Id, [], 14, "group", day.AddHours(5));
    Check(earlyLong.EarlyFee == 100 && earlyLong.LateFee == 0, "early-start fee takes priority even when a package ends after regular closing");
    using var barrier = new Barrier(2);
    var attempts = Enumerable.Range(0, 2).Select(i => Task.Run(() => { barrier.SignalAndWait(); try { return flow.Create(user.Id, item.Id, [], day.AddHours(19).AddMinutes(i * 15), "Synthetic address", "Quezon City", late.RequiredDownPayment, "GCash", "LATE-RACE-" + i, null, true, 1, "auto", true, late.FinalTotal, late.RequiredDownPayment); } catch (InvalidOperationException) { return null; } })).ToArray();
    Task.WaitAll(attempts);
    var winners = attempts.Select(t => t.Result).OfType<Booking>().ToArray();
    Check(winners.Length == 1 && winners[0].LateFeeSnapshot == 275, "overlapping late reservations have one winner with a fee snapshot");
    var booking = winners[0];
    Reject(() => flow.CheckSlot(booking.End.AddMinutes(14), 30), "late reservation preparation buffer blocks conflicts");
    demo.Settings.LateFee = 500; demo.Settings.LateFeeEnabled = false;
    Check(booking.Total == late.FinalTotal && booking.LateFeeSnapshot == 275, "policy changes preserve booking snapshot");
    flow.AdminAction(booking, "verify", reviewer.Id, "", 0, "");
    Reject(() => flow.Reschedule(booking, user.Id, day.AddDays(1).AddHours(15)), "reschedule cannot silently remove late fee");
    Console.WriteLine($"All {checks} late fee checks passed."); return;
}
if (args.Contains("--fees-only"))
{
    var demo = new DemoStore(); var flow = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == Roles.Customer); var reviewer = demo.Users.First(u => u.Role == Roles.Admin);
    var start = DateTime.Today.AddDays(20).AddHours(5);
    demo.Settings.EarlyStartHour = 5; demo.Settings.EarlyFee = 321; demo.Settings.EarlyFeeEnabled = true;
    foreach (var item in demo.Services.Where(s => s.Active && !s.Archived))
        Check(flow.Quote(item.Id, [], 1, "auto", start).EarlyFee == 321, $"early fee applies to {item.Name}");
    var item1 = demo.Services.First(s => s.Active && !s.Archived);
    var quote = flow.Quote(item1.Id, [], 1, "auto", start);
    var normal = flow.Quote(item1.Id, [], 1, "auto", start.Date.AddHours(demo.Settings.OpenHour));
    Check(quote.FinalTotal == normal.FinalTotal + 321 && quote.RequiredDownPayment == Money.Percentage(quote.FinalTotal, demo.Settings.DownPaymentPercent), "fee changes total and minimum payment");
    Check(flow.Quote(item1.Id, [], 1, "auto", start.AddMinutes(-1)).EarlyFee == 0 && normal.EarlyFee == 0, "early interval includes earliest start and excludes opening");
    flow.CheckSlot(start, quote.DurationMinutes);
    Reject(() => flow.CheckSlot(start.AddMinutes(-15), quote.DurationMinutes), "starts before early boundary unavailable");
    Reject(() => flow.Create(user.Id, item1.Id, [], start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "FEE-STALE", null, true, 1, "auto", true, normal.FinalTotal), "stale quote rejected before reservation");
    var booking = flow.Create(user.Id, item1.Id, [], start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "FEE-OK", null, true, 1, "auto", true, quote.FinalTotal);
    demo.Settings.EarlyFee = 500; demo.Settings.EarlyFeeEnabled = false;
    Check(booking.EarlyFeeSnapshot == 321 && booking.Total == quote.FinalTotal, "booking retains historical fee and total");
    flow.AdminAction(booking, "verify", reviewer.Id, "", 0, "");
    Check(booking.Status == "Confirmed", "existing pending booking can be verified after policy change");
    Reject(() => flow.Reschedule(booking, user.Id, start.AddDays(1).Date.AddHours(10)), "reschedule cannot silently change fee");
    Console.WriteLine($"All {checks} Demo fee checks passed."); return;
}
if (args.Contains("--travel-only"))
{
    var routes = new Dictionary<(string, string), TimeSpan> {
        [("SYNTHETIC:BASE", "SYNTHETIC:A")] = TimeSpan.FromHours(2),
        [("SYNTHETIC:A", "SYNTHETIC:B")] = TimeSpan.FromHours(2)
    };
    var estimator = new SyntheticTravelEstimator(routes);
    var planning = new OfflineTravelScheduler(estimator);
    var start = new DateTimeOffset(2030, 1, 2, 8, 0, 0, TimeSpan.FromHours(8));
    var anchor = new TravelAnchor("SYNTHETIC:BASE", start.AddHours(-3));
    var stop = new TravelStop("SYNTHETIC:A", start, TimeSpan.FromHours(4), TimeSpan.Zero);
    var result = planning.Review(anchor, stop, null, true, RouteMode.Driving, TimeSpan.Zero).Result;
    Check(result.Status == TravelReviewStatus.SyntheticFit && result.Projection?.Departure == start.AddHours(-2) && result.Projection.MakeupEnd == start.AddHours(4), "synthetic 8 AM start yields 6 AM departure and noon finish");
    Check(result.Projection!.EstimateStatus == RouteEstimateStatus.Synthetic && result.Projection.Evidence.Synthetic, "synthetic estimates are explicitly labeled");
    var next = new TravelStop("SYNTHETIC:B", start.AddHours(5), TimeSpan.FromHours(1), TimeSpan.Zero);
    Check(planning.Review(anchor, stop, next, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.SyntheticConflict, "two-hour next trip cannot fit before 1 PM after noon finish");
    Check(planning.Review(anchor, stop, next with { MakeupStart = start.AddHours(6) }, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.SyntheticFit, "touching next departure boundary fits synthetic schedule");
    Check(planning.Review(anchor, stop with { PreparationBuffer = TimeSpan.FromMinutes(15) }, next with { MakeupStart = start.AddHours(6) }, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.SyntheticConflict, "preparation buffer prevents an otherwise touching next trip");
    Check(planning.Review(anchor with { ReadyAt = start.AddHours(-1) }, stop, null, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.SyntheticConflict, "previous appointment readiness is respected");
    Check(planning.Review(anchor, stop, next with { LocationReference = "SYNTHETIC:UNKNOWN" }, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.Unavailable, "unknown route never becomes zero-minute travel");
    Check(planning.Review(anchor, stop, null, false, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.InvalidRequest, "unknown next appointment state fails closed");
    Check(estimator.Estimate(new RouteRequest("real address", "SYNTHETIC:A", null, start, RouteMode.Driving, TimeSpan.Zero)).Result.Status == RouteEstimateStatus.InvalidRequest, "fake estimator rejects nonsynthetic locations");
    var demo = new DemoStore(); var serviceForTravel = new BookingService(demo, demo, demo, demo, demo);
    var serviceItem = demo.Services[0]; var addOn = demo.AddOns[0];
    var travelQuote = serviceForTravel.Quote(serviceItem.Id, [addOn.Id, addOn.Id], 4, "group");
    Check(travelQuote.DurationMinutes == serviceItem.Minutes * 4 + addOn.Minutes * 2, "travel-planning input can use existing quantity-based service duration");
    Console.WriteLine($"All {checks} offline synthetic travel checks passed; no route is verified.");
    return;
}
if (args.Contains("--reservation-only"))
{
    var demo = new DemoStore();
    demo.Settings.BufferMinutes = 15;
    var flow = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == Roles.Customer);
    var reviewer = demo.Users.First(u => u.Role == Roles.Admin);
    var reservationOwner = demo.Users.First(u => u.Role == Roles.SuperAdmin);
    var look = demo.Services[0];
    var when = DateTime.Today.AddDays(20).AddHours(10);
    var quote = flow.Quote(look.Id, []);
    Booking Submit(DateTime start, string reference, decimal? amount = null) => flow.Create(user.Id, look.Id, [], start, "Synthetic address", "Quezon City", amount ?? quote.RequiredDownPayment, "GCash", reference, null, true, 1, "auto", true);
    using var barrier = new Barrier(2);
    var attempts = Enumerable.Range(0, 2).Select(i => Task.Run(() => { barrier.SignalAndWait(); try { return Submit(when.AddMinutes(i * 15), "SYNTHETIC-RACE-" + i); } catch (InvalidOperationException) { return null; } })).ToArray();
    Task.WaitAll(attempts);
    var winners = attempts.Select(t => t.Result).OfType<Booking>().ToArray();
    Check(winners.Length == 1, "overlapping simultaneous Demo submissions have exactly one winner");
    var reservation = winners[0];
    Check(reservation.Status == "Pending Payment" && reservation.BufferMinutesSnapshot == 15, "pending booking reserves a snapshotted buffer");
    Reject(() => flow.CheckSlot(reservation.Start.AddMinutes(1), 60), "pending service interval blocks overlaps");
    Reject(() => demo.AddBlock(new(Guid.NewGuid(), reservation.Start.AddMinutes(30), reservation.End.AddMinutes(1), "Synthetic"), reviewer.Id), "staff block cannot overlap a pending reservation");
    demo.Settings.BufferMinutes = 30;
    Reject(() => flow.CheckSlot(reservation.End.AddMinutes(14), 60), "existing snapshotted buffer remains reserved after settings change");
    flow.CheckSlot(reservation.End.AddMinutes(15), 60);
    Check(!flow.DemoOpenWindows(reservation.Start.Date).Any(w => w.From <= reservation.Start && w.To > reservation.Start), "open windows exclude the pending interval");
    var reservationCalendar = flow.GetAvailability(new DateTime(when.Year, when.Month, 1), 60);
    Check(reservationCalendar.Days.Single(x => x.Date == reservation.Start.ToString("yyyy-MM-dd")).Slots.Single(x => x.Start == reservation.Start.ToString("yyyy-MM-ddTHH:mm")).Available == false, "customer availability excludes pending slot");
    flow.AdminAction(reservation, "reject", reviewer.Id, "Synthetic rejection", 0, "");
    flow.CheckSlot(reservation.Start, 60);
    Check(true, "rejection releases the pending interval");
    var confirmed = Submit(reservation.Start, "SYNTHETIC-CONFIRMED");
    flow.AdminAction(confirmed, "verify", reviewer.Id, "", 0, "");
    Reject(() => flow.CheckSlot(confirmed.Start, 60), "confirmed appointment blocks overlapping starts");
    var destination = when.AddDays(1);
    var rescheduleOffer = flow.OfferReschedule(confirmed, user.Id, destination);
    flow.ConsentReschedule(confirmed, user.Id, rescheduleOffer.Id, true);
    Reject(() => flow.CheckSlot(confirmed.Start, 60), "original confirmed interval remains reserved after requesting reschedule");
    var competing = Submit(destination, "SYNTHETIC-DESTINATION");
    Reject(() => flow.AdminAction(confirmed, "reschedule", reservationOwner.Id, "", 0, ""), "approval rejects a newly occupied replacement");
    Check(confirmed.Start == reservation.Start && confirmed.RequestedStart == destination, "failed approval keeps the original reservation and request");
    flow.AdminAction(competing, "reject", reviewer.Id, "Synthetic rejection", 0, "");
    flow.AdminAction(confirmed, "reschedule", reservationOwner.Id, "", 0, "");
    flow.CheckSlot(reservation.Start, 60);
    Reject(() => flow.CheckSlot(destination, 60), "approved replacement is reserved");
    flow.Cancel(confirmed, user.Id, true);
    flow.CheckSlot(destination, 60);
    Check(confirmed.Status == "Cancelled" && confirmed.CancellationFee == 200, "cancellation retains its policy and releases the reservation");
    Reject(() => Submit(destination.AddDays(1), "SYNTHETIC-BAD-PAYMENT", 0), "failed submission creates no reservation");
    flow.CheckSlot(destination.AddDays(1), 60);
    var extra = demo.AddOns[0];
    var groupQuote = flow.Quote(look.Id, [extra.Id, extra.Id], 4, "group");
    var groupStart = destination.AddDays(3).Date.AddHours(8);
    var groupReservation = flow.Create(user.Id, look.Id, [extra.Id, extra.Id], groupStart, "Synthetic address", "Quezon City", groupQuote.RequiredDownPayment, "GCash", "SYNTHETIC-GROUP", null, true, 4, "group", true);
    Check((groupReservation.End - groupReservation.Start).TotalMinutes == look.Minutes * 4 + extra.Minutes * 2, "group reservation uses service and quantity-based add-on duration");
    Reject(() => flow.CheckSlot(groupReservation.End.AddMinutes(1), 60), "group reservation also blocks its preparation buffer");
    Console.WriteLine($"All {checks} synthetic reservation checks passed.");
    return;
}
var oldAvatars = new DemoAvatarStore("unused");
var presentationAvatars = new DemoAvatarStore("unused", true);
foreach (var (role, oldEmail, newEmail) in new[] { ("Customer", "customer@lumiere.demo", "presentation-customer@example.test"), ("Admin", "admin@lumiere.demo", "presentation-admin@example.test"), ("SuperAdmin", "owner@lumiere.demo", "presentation-owner@example.test") })
{
    var oldUser = new AppUser { Role = role, Email = oldEmail };
    var newUser = new AppUser { Role = role, Email = newEmail };
    Check(oldAvatars.Supports(oldUser) && !oldAvatars.Supports(newUser) && presentationAvatars.Supports(newUser) && !presentationAvatars.Supports(oldUser), $"{role} avatar mapping is restricted to the active seeded identity");
}
// Focused Demo-only regression; also runs as part of the full workflow suite.
{
    var demo = new DemoStore();
    var bookingService = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == "Customer");
    var look = demo.Services[0];
    var extra = demo.AddOns[0];
    var start = DateTime.Today.AddDays(5).AddHours(8);
    var originalBuffer = demo.Settings.BufferMinutes;
    var history = System.Text.Json.JsonSerializer.Serialize(demo.Bookings);
    var paymentHistory = System.Text.Json.JsonSerializer.Serialize(demo.Payments);
    // Non-zero informational travel costs must still be excluded from online totals.
    demo.Settings.TravelFee = 275;
    var quote = bookingService.Quote(look.Id, [extra.Id], 4, "group");
    var before = demo.Now;
    var created = bookingService.Create(user.Id, look.Id, [extra.Id], start, "78I Isolated Demo Street", "Quezon City",
        quote.RequiredDownPayment, "GCash", "LOCAL-78I-ACK", null, true, 4, "group", true);
    var after = demo.Now;
    Check(created.TransportationPolicyAcknowledgedAt.HasValue, "Demo stores accepted transportation-policy timestamp");
    Check(created.TransportationPolicyAcknowledgedAt >= before && created.TransportationPolicyAcknowledgedAt <= after,
        "Demo acknowledgment uses current creation time, not appointment time");
    Check(created.TransportationPolicyAcknowledgedAt!.Value.Kind == before.Kind, "Demo acknowledgment preserves the store's timestamp convention");
    Check(created.TravelFee == 0 && created.Total == quote.FinalTotal
        && created.Total == created.OriginalTotal - created.GroupDiscount + extra.Price, "Transportation remains separate from discounted booking totals");
    Check(created.RequiredDownPayment == quote.RequiredDownPayment && created.DownPaymentPercentSnapshot == demo.Settings.DownPaymentPercent
        && created.BalanceAfterDeposit == quote.RemainingBalance, "Acknowledgment does not change deposit or balance snapshots");
    Check(created.Service == new PriceSnapshot(look.Id, look.Name, look.Price, look.Minutes)
        && created.AddOns.Single() == new PriceSnapshot(extra.Id, extra.Name, extra.Price, extra.Minutes)
        && created.People == 4 && created.GroupPrice == quote.GroupPrice && created.AppliedPricePerPerson == quote.AppliedPricePerPerson,
        "Service, add-on and group-price snapshots are unchanged");
    Check(created.End == start.AddMinutes(quote.DurationMinutes) && demo.Settings.BufferMinutes == originalBuffer,
        "Acknowledgment does not change session duration or travel buffer");
    Check(demo.Payments.Single(p => p.BookingId == created.Id).Amount == quote.RequiredDownPayment,
        "Demo submitted payment retains the exact quoted deposit");
    Check(System.Text.Json.JsonSerializer.Serialize(demo.Bookings.Where(b => b.Id != created.Id)) == history
        && System.Text.Json.JsonSerializer.Serialize(demo.Payments.Where(p => p.BookingId != created.Id)) == paymentHistory,
        "Existing Demo bookings, acknowledgment values and payment history are untouched");
    var timestamp = created.TransportationPolicyAcknowledgedAt;
    var bookingsBeforeRejection = System.Text.Json.JsonSerializer.Serialize(demo.Bookings);
    var paymentsBeforeRejection = System.Text.Json.JsonSerializer.Serialize(demo.Payments);
    foreach (var consent in new[] { (Agreed: true, Travel: false), (Agreed: false, Travel: true), (Agreed: false, Travel: false) })
        Reject(() => bookingService.Create(user.Id, look.Id, [], start.AddDays(1), "78I Isolated Demo Street", "Quezon City",
            bookingService.Quote(look.Id, []).RequiredDownPayment, "GCash", "LOCAL-78I-REJECT", null, consent.Agreed, 1, "auto", consent.Travel),
            $"Missing required consent is rejected (booking={consent.Agreed}, transportation={consent.Travel})");
    Check(System.Text.Json.JsonSerializer.Serialize(demo.Bookings) == bookingsBeforeRejection
        && System.Text.Json.JsonSerializer.Serialize(demo.Payments) == paymentsBeforeRejection
        && created.TransportationPolicyAcknowledgedAt == timestamp, "Rejected submissions do not change existing records or acknowledgment timestamps");
}
if (args.Contains("--transportation-only"))
{
    var demo = new DemoStore();
    var serviceCheck = new BookingService(demo, demo, demo, demo, demo);
    var person = demo.Users.First(u => u.Role == "Customer");
    var look = demo.Services[0]; var extra = demo.AddOns[0];
    var quote = serviceCheck.Quote(look.Id, [extra.Id, extra.Id], 4, "group");
    Check(quote.ExtrasTotal == extra.Price * 2 && quote.DurationMinutes == look.Minutes * 4 + extra.Minutes * 2, "quantity pricing and duration use two add-ons");
    Reject(() => serviceCheck.Quote(look.Id, [extra.Id, extra.Id], 1, "auto"), "add-on quantity cannot exceed party size");
    demo.Settings.OpenHour = 0; demo.Settings.CloseHour = 23; demo.Settings.BufferMinutes = 0;
    var booking = serviceCheck.Create(person.Id, look.Id, [extra.Id, extra.Id], DateTime.Today.AddDays(10), "Synthetic address", "Quezon City", quote.FinalTotal, "GCash", "SYNTHETIC-QUANTITY", null, true, 4, "group", true);
    Check(booking.Total == quote.FinalTotal && booking.End == booking.Start.AddMinutes(quote.DurationMinutes) && booking.AddOns.Single().Quantity == 2, "booking snapshots quantity and duration");
    Check(serviceCheck.Paid(booking) == 0 && demo.Payments.Single(p => p.BookingId == booking.Id).Amount == quote.FinalTotal, "full claimed amount remains unverified");
    Reject(() => serviceCheck.Create(person.Id, look.Id, [], DateTime.Today.AddDays(11), "Synthetic address", "Quezon City", 0, "GCash", "SYNTHETIC-LOW", null, true, 1, "auto", true), "below-minimum claim rejected");
    var individual = serviceCheck.Quote(look.Id, []);
    Reject(() => serviceCheck.Create(person.Id, look.Id, [], DateTime.Today.AddDays(11), "Synthetic address", "Quezon City", individual.FinalTotal + 1, "GCash", "SYNTHETIC-HIGH", null, true, 1, "auto", true), "above-total claim rejected");
    serviceCheck.AdminAction(booking, "verify", demo.Users.First(u => u.Role == "Admin").Id, "", 0, "");
    Check(serviceCheck.Paid(booking) == quote.FinalTotal && booking.Status == "Confirmed", "explicit review verifies full claimed payment");
    Console.WriteLine($"All {checks} focused Demo transportation checks passed.");
    return;
}
var keyStoreSql = File.ReadAllText(Path.Combine("Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "DataProtectionKeys.sql"));
Check(keyStoreSql.Contains("ENABLE ROW LEVEL SECURITY", StringComparison.OrdinalIgnoreCase)
    && keyStoreSql.Contains("FROM PUBLIC, anon, authenticated", StringComparison.OrdinalIgnoreCase),
    "Data Protection key table enables RLS and revokes browser-role access");
Check(keyStoreSql.Contains("data_protection_keys", StringComparison.OrdinalIgnoreCase)
    && !keyStoreSql.Contains("FORCE ROW LEVEL SECURITY", StringComparison.OrdinalIgnoreCase),
    "Data Protection table is dedicated to the server-owned database role");
Check(File.Exists(Path.Combine("Backend", "GlamByMariya.Infrastructure", "Migrations", "Public", "20261017130000_DataProtectionKeys.cs")),
    "Data Protection persistence migration is present and additive");
var connectionConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:MakeupDatabase"] = "postgresql://postgres.independent-placeholder:example%40password@aws-0-region.pooler.supabase.com:5432/postgres",
    ["Database:ProjectId"] = "independent-placeholder",
}).Build();
var parsedConnection = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(connectionConfig));
Check(parsedConnection.Host == "aws-0-region.pooler.supabase.com" && parsedConnection.Port == 5432
    && parsedConnection.Database == "postgres" && parsedConnection.SslMode == SslMode.VerifyFull,
    "Supabase Session Pooler URI parses host/port/database and requires VerifyFull");
connectionConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:MakeupDatabase"] = "tcp://postgres.independent-placeholder:example%40password@aws-0-region.pooler.supabase.com:5432/postgres",
    ["Database:ProjectId"] = "independent-placeholder",
}).Build();
parsedConnection = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(connectionConfig));
Check(parsedConnection.Host == "aws-0-region.pooler.supabase.com" && !parsedConnection.Host.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
    && parsedConnection.SslMode == SslMode.VerifyFull,
    "tcp://-prefixed pooler URI normalizes to the hostname without weakening TLS");
connectionConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:MakeupDatabase"] = "Host=tcp://aws-0-region.pooler.supabase.com:5432;Database=postgres;Username=postgres.independent-placeholder;Password=example",
    ["Database:ProjectId"] = "independent-placeholder",
}).Build();
parsedConnection = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(connectionConfig));
Check(parsedConnection.Host == "aws-0-region.pooler.supabase.com" && parsedConnection.Port == 5432
    && parsedConnection.SslMode == SslMode.VerifyFull,
    "tcp:// prefix in a key/value Host normalizes without changing port or TLS verification");
Booking Create(DateTime time, decimal? deposit = null, string area = "Quezon City") => workflow.Create(customer.Id, service.Id, [], time, "24 Sample Street", area, deposit ?? workflow.Quote(service.Id, []).RequiredDownPayment, "GCash", "TEST-" + Guid.NewGuid(), null, true, 1, "auto", true);
var time = DateTime.Today.AddDays(5).AddHours(10);
Reject(() => Create(time, 299), "Down payment below 50% rejected");
Reject(() => workflow.Create(customer.Id, service.Id, [], time, "24 Sample Street", "Quezon City", workflow.Quote(service.Id, []).RequiredDownPayment, "GCash", "NO-TRANSPORT-POLICY", null, true, 1, "auto", false), "Transportation policy acknowledgment is required");
Reject(() => Create(time, 99999), "Overpayments rejected: exact half required");
Reject(() => Create(time, area: "Outside area"), "Unsupported service area rejected");
Reject(() => Create(time.Date.AddHours(17)), "Session plus travel buffer must fit within hours");
var b = Create(time);
Check(b.Status == "Pending Payment" && b.PaymentStatus == "Pending Verification", "Booking awaits down payment verification");
Reject(() => workflow.AdminAction(b, "approve", admin.Id, "", 0, ""), "Cannot approve an unverified deposit");
workflow.AdminAction(b, "verify", admin.Id, "", 0, "");
workflow.AdminAction(b, "approve", admin.Id, "", 0, "");
Check(b.Status == "Confirmed" && workflow.Paid(b) == b.RequiredDownPayment && b.RemainingBalance == b.BalanceAfterDeposit, "Verification confirms the booking and records the outstanding half");
var verifiedAt = store.Payments.Single(p => p.BookingId == b.Id).VerifiedAt;
var paymentCount = store.Payments.Count;
workflow.AdminAction(b, "verify", admin.Id, "", 0, "");
Check(paymentCount == store.Payments.Count && verifiedAt == store.Payments.Single(p => p.BookingId == b.Id).VerifiedAt, "Repeated verification is idempotent");
Reject(() => Create(time.AddMinutes(75)), "Travel buffer prevents overlapping appointment");
var oldPrice = b.Total;
service.Price += 1000;
Check(b.Total == oldPrice, "Booked prices survive catalog edits");
var c = Create(time.AddDays(1));
Reject(() => Create(time.AddDays(1)), "A pending request reserves its interval before payment verification");
var d = Create(time.AddDays(1).AddHours(3));
workflow.AdminAction(c, "verify", admin.Id, "", 0, "");
workflow.AdminAction(c, "approve", admin.Id, "", 0, "");
Check(d.Status == "Pending Payment" && workflow.Paid(d) == 0, "An independent pending request remains unverified");
Reject(() => workflow.OfferReschedule(b, customer.Id, c.Start), "Conflicting reschedule is rejected during quotation");
Check(b.Start == time, "Original appointment retained on failed reschedule");
Reject(() => workflow.OfferReschedule(d, customer.Id, time.AddDays(2)), "Pending booking cannot be rescheduled");
Reject(() => workflow.OfferReschedule(b, admin.Id, time.AddDays(2)), "Only the customer can request a reschedule quote");
var workflowOffer = workflow.OfferReschedule(b, customer.Id, time.AddDays(2));
Reject(() => workflow.AdminAction(b, "reschedule", workflowOwner.Id, "", 0, ""), "SuperAdmin cannot approve without customer consent");
Reject(() => workflow.ConsentReschedule(b, customer.Id, workflowOffer.Id, false), "Explicit consent is required");
Reject(() => workflow.ConsentReschedule(b, admin.Id, workflowOffer.Id, true), "Another role cannot consent for the customer");
workflow.ConsentReschedule(b, customer.Id, workflowOffer.Id, true);
Check(b.Start == time && b.RequestedStart == workflowOffer.ProposedStart && b.PendingReschedule != null, "Customer consent retains the original appointment pending approval");
Reject(() => workflow.AdminAction(b, "reschedule", admin.Id, "", 0, ""), "Admin cannot approve a reschedule");
var competingBlock = new AvailabilityBlock(Guid.NewGuid(), workflowOffer.ProposedStart, workflowOffer.ProposedStart.AddHours(3), "Synthetic conflict after consent");
store.Blocks.Add(competingBlock);
Reject(() => workflow.AdminAction(b, "reschedule", workflowOwner.Id, "", 0, ""), "SuperAdmin approval rechecks conflicts after consent");
Check(b.Start == time && b.PendingReschedule != null, "Failed approval preserves original appointment and customer consent");
store.Blocks.Remove(competingBlock);
workflow.AdminAction(b, "reschedule", workflowOwner.Id, "", 0, "");
Check(b.Start == time.AddDays(2) && b.RequestedStart == null && b.Total == oldPrice && workflow.Paid(b) == b.RequiredDownPayment, "Reschedule retains prices and payment");
Check(b.PendingReschedule == null && b.ApprovedReschedules.Count == 1 && b.ApprovedReschedules[0].Quote.ProposedStart == b.Start, "Approved booking details retain the agreed reschedule");
var declineOffer = workflow.OfferReschedule(b, customer.Id, time.AddDays(4));
workflow.ConsentReschedule(b, customer.Id, declineOffer.Id, true);
workflow.AdminAction(b, "decline", admin.Id, "Unavailable", 0, "");
Check(b.Start == time.AddDays(2) && b.RequestedStart == null && b.PendingReschedule == null, "Declined request preserves the approved appointment and clears consent");
store.Blocks.Add(new(Guid.NewGuid(), time.AddDays(3), time.AddDays(3).AddHours(3), "Unavailable"));
Reject(() => Create(time.AddDays(3)), "Unavailable time blocks honored");
workflow.AdminAction(d, "reject", admin.Id, "Address issue", 0, "");
Check(d.RefundDue == workflow.Paid(d) && d.CancellationFee == 0, "Admin rejection refunds verified payments only and does not charge a cancellation fee");
Reject(() => workflow.AdminAction(c, "complete", workflowOwner.Id, "", 0, "", true), "Future appointment cannot be completed");
c.Start = DateTime.Today.AddDays(-1).AddHours(10);
Reject(() => workflow.AdminAction(c, "complete", admin.Id, "", 0, "", true), "Admin cannot complete an appointment");
Reject(() => workflow.AdminAction(c, "complete", workflowOwner.Id, "", 0, ""), "Completion requires confirmation");
var paidBeforeCompletion = workflow.Paid(c);
workflow.AdminAction(c, "complete", workflowOwner.Id, "", 0, "", true);
Check(c.Status == "Completed" && workflow.Paid(c) == paidBeforeCompletion, "Completion does not invent a final payment");
Reject(() => workflow.Cancel(b, customer.Id, false), "Cancellation requires explicit confirmation");
Reject(() => workflow.Cancel(b, admin.Id, true), "Customer can only cancel their own booking");
workflow.Cancel(b, customer.Id, true);
Check(b.Status == "Cancelled" && b.CancellationFee == 200 && b.RefundDue == b.RequiredDownPayment - 200, "Cancellation deducts exactly â‚±200 from verified down payment");
workflow.AdminAction(b, "refund", admin.Id, "", 100, "TRANSFER-1");
Check(b.Refunded == 100 && b.PaymentStatus == "Refunded", "Manual refund recorded");
Reject(() => workflow.AdminAction(b, "refund", admin.Id, "", b.RefundDue, "TRANSFER-2"), "Over-refund prevented");
workflow.CheckSlot(b.Start, (int)(b.End - b.Start).TotalMinutes);
Check(true, "Cancelled appointment releases reserved slot");
Reject(() => workflow.Cancel(b, customer.Id, true), "Repeated cancellation rejected");
Check(store.Notifications.Any(n => n.UserId == customer.Id) && store.AuditLogs.Any(), "Notifications and audit events created");
var availabilityStore = new DemoStore();
availabilityStore.Bookings.Clear();
availabilityStore.Payments.Clear();
var availability = new BookingService(availabilityStore, availabilityStore, availabilityStore, availabilityStore, availabilityStore);
var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
var calendar = availability.GetAvailability(first, 60);
Check(calendar.Days.Count == DateTime.DaysInMonth(first.Year, first.Month), "Calendar contains every day of the requested month");
var calendarJson = System.Text.Json.JsonSerializer.Serialize(calendar, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
using (var calendarDocument = System.Text.Json.JsonDocument.Parse(calendarJson))
    Check(calendarDocument.RootElement.TryGetProperty("days", out var jsonDays) && jsonDays.GetArrayLength() == calendar.Days.Count
        && calendarDocument.RootElement.GetProperty("month").GetString() == calendar.Month,
        "Availability endpoint JSON uses the camel-case month/days shape expected by JavaScript");
Check(calendar.Days.All(day => day.Status == "available" && day.AvailableCount == 34), "Open dates have all 15-minute starts that fit session and buffer");
Check(calendar.Days[0].Slots.Last().Start.EndsWith("T16:15"), "Closing boundary includes exactly fitting slot");
var currentCalendar = availability.GetAvailability(DateTime.Today, 60);
Check(currentCalendar.Days.Where(day => DateTime.Parse(day.Date) < DateTime.Today).All(day => day.Status == "past" && day.AvailableCount == 0 && day.Slots.Count == 0), "Past dates have no selectable slots");
Check(currentCalendar.Days.SelectMany(d => d.Slots).Where(s => DateTime.Parse(s.Start) <= DateTime.Now).All(s => !s.Available), "Elapsed times today are disabled");
availabilityStore.Blocks.Add(new(Guid.NewGuid(), first.AddDays(1), first.AddDays(2), "Whole day blocked"));
calendar = availability.GetAvailability(first, 60);
Check(calendar.Days[1].Status == "unavailable" && calendar.Days[1].AvailableCount == 0, "Whole-day artist blocks disable dates");
var full = new Booking { CustomerId = availabilityStore.Users.First(u => u.Role == "Customer").Id, Start = first.AddDays(2).AddHours(8), Service = new(Guid.NewGuid(), "All-day session", 3000, 555), Status = "Confirmed" };
availabilityStore.Bookings.Add(full);
calendar = availability.GetAvailability(first, 60);
Check(calendar.Days[2].Status == "full" && calendar.Days[2].Slots.All(s => !s.Available), "Fully booked dates and times disabled");
var partial = new Booking { Start = first.AddDays(3).AddHours(10), Service = new(Guid.NewGuid(), "Short session", 1500, 60), Status = "Confirmed" };
availabilityStore.Bookings.Add(partial);
calendar = availability.GetAvailability(first, 60);
Check(calendar.Days[3].Status == "limited" && calendar.Days[3].AvailableCount is > 0 and < 34, "Partially booked dates are marked limited");
Check(!calendar.Days[3].Slots.First(s => s.Start.EndsWith("T11:30")).Available && calendar.Days[3].Slots.First(s => s.Start.EndsWith("T11:45")).Available, "Existing travel buffer and exact nonoverlap boundary respected");
foreach (var day in calendar.Days)
foreach (var slot in day.Slots.Where(s => s.Available)) availability.CheckSlot(DateTime.Parse(slot.Start), 60);
Check(true, "Calendar availability uses the same rules as booking submission");
availability.Cancel(full, full.CustomerId, true);
Check(availability.GetAvailability(first, 60).Days[2].Status == "available", "Cancellation immediately restores calendar availability");
Check(availability.GetAvailability(first, 60, partial.Id).Days[3].Status == "available", "Reschedule calendar excludes only its own reservation");
availabilityStore.Services[0].Minutes = 90;
Check(availability.SelectedDuration(availabilityStore.Services[0].Id, [availabilityStore.AddOns[1].Id, availabilityStore.AddOns[1].Id], 2, "group") == 270, "Calendar derives service and quantity-weighted add-on duration");
Check(availability.GetAvailability(first, 135).Days[0].AvailableCount < 34, "Longer services and add-ons reduce available starts");
availabilityStore.AddOns[1].Active = false;
Reject(() => availability.SelectedDuration(availabilityStore.Services[0].Id, [availabilityStore.AddOns[1].Id]), "Inactive add-ons cannot be used for availability");
Reject(() => availability.GetAvailability(first.AddMonths(12), 60), "Availability requests beyond supported month range rejected");
Reject(() => availability.GetAvailability(first.AddMonths(-2), 60), "Past month requests rejected");
Reject(() => availability.GetAvailability(first, 0), "Invalid duration rejected");
Reject(() => availability.CheckSlot(DateTime.MaxValue, 60), "Out-of-range dates fail validation without overflow");
var sampleService = new CatalogItem { Name = "Soft Glam", Price = 600, GroupPrice = 525, GroupMinimumPeople = 4, Minutes = 60 };
foreach (var sample in new[] { (1, 600m, 300m), (2, 1200m, 600m), (3, 1800m, 900m), (4, 2100m, 1050m), (5, 2625m, 1312.50m), (10, 5250m, 2625m) })
{
    var quote = BookingPricing.Calculate(sampleService, [], sample.Item1, 0, sample.Item1 >= sampleService.GroupMinimumPeople ? "group" : "auto");
    Check(quote.FinalTotal == sample.Item2 && quote.RequiredDownPayment == sample.Item3 && quote.RemainingBalance == sample.Item3, $"Soft Glam {sample.Item1} people: discounted total and 50/50 split match sample");
}
var ten = BookingPricing.Calculate(sampleService, [], 10, 0, "group");
Check(ten.OriginalTotal == 6000 && ten.GroupDiscount == 750 && ten.FinalTotal == 5250 && ten.RequiredDownPayment == 2625, "Ten-person summary discounts before halving");
var withExtras = BookingPricing.Calculate(sampleService, [new() { Price = 100, Minutes = 15 }], 5, 250, "group");
Check(withExtras.FinalTotal == 2725 && withExtras.RequiredDownPayment == 1362.50m && withExtras.DurationMinutes == 315, "Extras are included once before the 50% calculation and duration accounts for people; travel is excluded");
Reject(() => BookingPricing.Calculate(sampleService, [], 0, 0), "Zero people rejected");
Reject(() => BookingPricing.Calculate(sampleService, [], 21, 0), "Excessive party size rejected");
Check(BookingPricing.Calculate(sampleService, [], 3, 0, "group").FinalTotal == 1800, "Package below threshold uses regular per-person pricing");
Reject(() => BookingPricing.Calculate(sampleService, [], 4, 0, "tampered"), "Unknown package rejected");
var oddCent = BookingPricing.Calculate(new() { Name = "Odd-cent service", Price = 600.01m, Minutes = 30 }, [], 1, 0);
Check(oddCent.RequiredDownPayment == 300.01m && oddCent.RemainingBalance == 300m && oddCent.RequiredDownPayment + oddCent.RemainingBalance == oddCent.FinalTotal, "Odd-cent totals round the down payment to a cent without losing money");
var forty = BookingPricing.Calculate(sampleService, [], 4, 0, "group", 40);
Check(forty.FinalTotal == 2100 && forty.RequiredDownPayment == 840 && forty.RemainingBalance == 1260 && forty.DownPaymentPercent == 40, "â‚±2,100 at 40% requires â‚±840 down and â‚±1,260 balance");
var onePercent = BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 1);
Check(onePercent.RequiredDownPayment == 6 && onePercent.RemainingBalance == 594, "1% down payment of â‚±600 requires â‚±6 down");
var fullPercent = BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 100);
Check(fullPercent.RequiredDownPayment == 600 && fullPercent.RemainingBalance == 0, "100% down payment collects the entire total");
Reject(() => BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 0), "0% down payment rejected");
Reject(() => BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 101), "101% down payment rejected");
Reject(() => BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 40.5m), "Fractional down payment percentage rejected");
Check(BookingPricing.Calculate(new CatalogItem { Name = "Glamour Makeup", Price = 750, Minutes = 90 }, [], 1, 0, "auto", 40).RequiredDownPayment == 300, "Glamour Makeup â‚±750 at 40% requires â‚±300 down");
Check(BookingPricing.Calculate(new CatalogItem { Name = "Bridal Makeup", Price = 1500, Minutes = 120 }, [], 1, 0, "auto", 50).RequiredDownPayment == 750, "Bridal Makeup â‚±1,500 at 50% requires â‚±750 down");
var paymentStore = new DemoStore();
paymentStore.Bookings.Clear(); paymentStore.Payments.Clear(); paymentStore.Settings.TravelFee = 0;
var paymentWorkflow = new BookingService(paymentStore, paymentStore, paymentStore, paymentStore, paymentStore);
var pService = paymentStore.Services[0]; pService.Minutes = 30;
var pCustomer = paymentStore.Users.First(u => u.Role == "Customer");
var pAdmin = paymentStore.Users.First(u => u.Role == "Admin");
var pOwner = paymentStore.Users.First(u => u.Role == "SuperAdmin");
var pStart = DateTime.Today.AddDays(20).AddHours(8);
Booking GroupBooking(string reference, decimal deposit = 2625) => paymentWorkflow.Create(pCustomer.Id, pService.Id, [], pStart, "24 Sample Street", "Quezon City", deposit, "Maya", reference, null, true, 10, "group", true);
Reject(() => GroupBooking("UNDERPAYMENT", 2624.99m), "One-cent down payment shortfall rejected");
Reject(() => GroupBooking("OVERPAYMENT", 5250.01m), "Payment above the booking total is rejected");
var groupBooking = GroupBooking("GROUP-PAYMENT");
Check(groupBooking.Total == 5250 && groupBooking.RequiredDownPayment == 2625 && groupBooking.GroupDiscount == 750, "Discounted prices and down payment saved as booking snapshots");
Reject(() => paymentWorkflow.Create(pCustomer.Id, pService.Id, [], pStart.AddDays(1), "24 Sample Street", "Quezon City", 2625, "Maya", " group-payment ", null, true, 10, "group", true), "Duplicate payment references are blocked regardless of whitespace/case");
pService.Price = 900; pService.GroupPrice = 700; paymentStore.Settings.TravelFee = 500;
Check(groupBooking.Total == 5250 && groupBooking.RequiredDownPayment == 2625, "Editing catalog rates and travel fees does not change booked group prices");
paymentWorkflow.AdminAction(groupBooking, "verify", pAdmin.Id, "", 0, "");
var verifiedGroupPayment = paymentStore.Payments.Single();
Check(groupBooking.Status == "Confirmed" && groupBooking.VerifiedPaid == 2625 && groupBooking.RemainingBalance == 2625 && groupBooking.PaymentStatus == "Down Payment Verified", "Successful 50% verification atomically confirms group booking");
Parallel.For(0, 10, _ => paymentWorkflow.AdminAction(groupBooking, "verify", pAdmin.Id, "", 0, ""));
Check(paymentStore.Payments.Count == 1 && paymentWorkflow.Paid(groupBooking) == 2625, "Concurrent verification retries do not process payments twice");
var groupOffer = paymentWorkflow.OfferReschedule(groupBooking, pCustomer.Id, pStart.AddDays(1));
paymentWorkflow.ConsentReschedule(groupBooking, pCustomer.Id, groupOffer.Id, true);
paymentWorkflow.AdminAction(groupBooking, "reschedule", pOwner.Id, "", 0, "");
Check(groupBooking.Total == 5250 && groupBooking.RequiredDownPayment == 2625 && groupBooking.RemainingBalance == 2625, "Group reschedule retains discounted price and down payment");
paymentWorkflow.Cancel(groupBooking, pCustomer.Id, true);
Check(groupBooking.RefundDue == 2425 && groupBooking.CancellationFee == 200, "Group cancellation refunds verified half minus the existing â‚±200 fee");
var tampered = GroupBooking("TAMPERED", paymentWorkflow.Quote(pService.Id, [], 10, "group").RequiredDownPayment);
paymentStore.Payments.Single(p => p.BookingId == tampered.Id).Amount -= 1;
Reject(() => paymentWorkflow.AdminAction(tampered, "verify", pAdmin.Id, "", 0, ""), "Verification rechecks stored down payment amount against final price");
Check(tampered.Status == "Pending Payment" && tampered.VerifiedPaid == 0, "Failed verification preserves pending payment status");
// Owner-configurable down payment: change the policy to 40%; existing snapshots stay untouched.
paymentStore.Settings.DownPaymentPercent = 40;
var quote40 = paymentWorkflow.Quote(pService.Id, [], 10, "group");
Check(quote40.RequiredDownPayment == 2800 && quote40.RemainingBalance == 4200, "40% policy recalculates new quotations (â‚±2,800 down on â‚±7,000)");
var booking40 = paymentWorkflow.Create(pCustomer.Id, pService.Id, [], pStart.AddDays(5), "24 Sample Street", "Quezon City", quote40.RequiredDownPayment, "Maya", "POLICY-40", null, true, 10, "group", true);
Check(booking40.RequiredDownPayment == 2800 && booking40.DownPaymentPercentSnapshot == 40 && booking40.RemainingBalance == 7000, "New booking snapshots the 40% policy");
Check(groupBooking.RequiredDownPayment == 2625 && groupBooking.DownPaymentPercentSnapshot == 50, "Changing the policy does not alter existing booking snapshots");
paymentStore.Settings.DownPaymentPercent = 50;
Check(paymentWorkflow.Quote(pService.Id, [], 10, "group").RequiredDownPayment == 3500, "Restoring 50% applies to new quotations again");
// Exercise the configured Demo catalog without inventing or modifying any prices.
var matrixStore = new DemoStore();
var matrixWorkflow = new BookingService(matrixStore, matrixStore, matrixStore, matrixStore, matrixStore);
var matrixAddOns = matrixStore.AddOns.Where(x => x.Active && !x.Archived).ToArray();
foreach (var item in matrixStore.Services.Where(x => x.Active && !x.Archived))
{
    foreach (var count in new[] { 1, 2, 4, 5, 10, 20 })
    {
        var addOnIds = matrixAddOns.SelectMany(x => Enumerable.Repeat(x.Id, count)).ToArray();
        var extras = matrixAddOns.Sum(x => x.Price * count);
        if (count == 1)
        {
            var individualQuote = matrixWorkflow.Quote(item.Id, addOnIds, count, "auto");
            var individualExpected = item.Price + extras;
            Check(individualQuote.FinalTotal == individualExpected && individualQuote.RequiredDownPayment == Money.Percentage(individualExpected, matrixStore.Settings.DownPaymentPercent),
                $"Configured {item.Name}: individual total, maximum add-ons and down payment");
        }
        else
            Reject(() => matrixWorkflow.Quote(item.Id, addOnIds, count, "auto"), $"Individual booking rejects {count} people for {item.Name}");
        if (item.GroupPrice.HasValue)
        {
            var groupQuote = matrixWorkflow.Quote(item.Id, addOnIds, count, "group");
            var unit = count >= item.GroupMinimumPeople ? item.GroupPrice.Value : item.Price;
            var groupExpected = unit * count + extras;
            Check(groupQuote.FinalTotal == groupExpected && groupQuote.RequiredDownPayment == Money.Percentage(groupExpected, matrixStore.Settings.DownPaymentPercent),
                $"Configured {item.Name}, {count} people: package rate and down payment");
        }
        else
            Reject(() => matrixWorkflow.Quote(item.Id, addOnIds, count, "group"), $"Unconfigured {item.Name} package rate is rejected for {count} people");
        if (matrixAddOns.Length > 0)
            Reject(() => matrixWorkflow.Quote(item.Id, Enumerable.Repeat(matrixAddOns[0].Id, count + 1).ToArray(), count, count == 1 ? "auto" : "group"),
                $"Configured {item.Name}, {count} people: add-on quantity cannot exceed party size");
    }
}
var qrRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "qr-tests-" + Guid.NewGuid());
try
{
    var qr = new PaymentQrService(qrRoot);
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6cWQAAAAASUVORK5CYII=");
    Check(qr.GetOptions().Count == 3 && qr.GetOptions().All(o => o.ImageUrl == null), "Payment QR options start empty for each supported method");
    qr.Save("GCash", png);
    var gcash = qr.Read("GCash")!.Value;
    Check(gcash.ContentType == "image/png" && gcash.Bytes.SequenceEqual(png), "Uploaded GCash QR image can be read with correct content type");
    Check(qr.GetOptions().Single(o => o.Method == "GCash").ImageUrl!.StartsWith("/Payments/Qr?method=GCash&v=") && qr.Read("Maya") == null, "QR is mapped only to its selected payment method");
    qr.Save("Maya", png);
    qr.Save("Bank Transfer", png);
    Check(qr.GetOptions().All(o => o.ImageUrl != null) && qr.GetOptions().Last().ImageUrl!.Contains("Bank%20Transfer"), "All three payment QR methods can be configured independently");
    Check(new PaymentQrService(qrRoot).Read("GCash")!.Value.Bytes.SequenceEqual(png), "Payment QR image persists across service restarts");
    Reject(() => qr.Save("Cash", png), "Unsupported QR payment method rejected");
    Reject(() => qr.Save("../gcash", png), "QR upload path traversal rejected");
    Reject(() => qr.Read("../gcash"), "QR read path traversal rejected");
    Reject(() => qr.Save("GCash", []), "Empty QR upload rejected");
    Reject(() => qr.Save("GCash", new byte[5_000_001]), "QR upload above 5 MB rejected");
    Reject(() => qr.Save("GCash", System.Text.Encoding.UTF8.GetBytes("<svg>not a payment QR</svg>")), "Non-JPG/PNG QR files rejected");
    Check(qr.Read("GCash")!.Value.Bytes.SequenceEqual(png), "Rejected QR upload keeps the previous image intact");
    qr.Save("GCash", png);
    Check(Directory.GetFiles(Path.Combine(qrRoot, "App_Data", "PaymentQr"), "gcash.*").Length == 1, "Replacing a QR leaves only the current image");
    qr.Remove("GCash");
    Check(qr.Read("GCash") == null && qr.Read("Maya") != null, "Removing one QR preserves other payment methods");
}
finally { if (Directory.Exists(qrRoot)) Directory.Delete(qrRoot, recursive: true); }
var catalogRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "catalog-image-tests-" + Guid.NewGuid());
try
{
    var catalog = new CatalogImageService(catalogRoot);
    var catalogServiceId = Guid.NewGuid();
    var sharedId = Guid.NewGuid();
    var pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6cWQAAAAASUVORK5CYII=");
    Check(new CatalogItem().VisualWord == "", "Catalog items default to an empty overlay word");
    Check(catalog.GetImageUrl("service", catalogServiceId) == null && catalog.Read("service", catalogServiceId) == null, "Catalog picture starts empty without uploads");
    catalog.Save("service", catalogServiceId, pixel);
    var saved = catalog.Read("service", catalogServiceId)!.Value;
    Check(saved.ContentType == "image/png" && saved.Bytes.SequenceEqual(pixel), "Uploaded service picture can be read with correct content type");
    Check(catalog.GetImageUrl("service", catalogServiceId)!.StartsWith($"/CatalogImages/Image?kind=service&id={catalogServiceId}&v=") && catalog.Read("addon", catalogServiceId) == null, "Catalog image URL maps only to its kind and item");
    catalog.Save("service", sharedId, pixel);
    catalog.Save("addon", sharedId, pixel);
    Check(catalog.Read("service", sharedId) != null && catalog.Read("addon", sharedId) != null, "Service and add-on pictures coexist for the same item id");
    Check(new CatalogImageService(catalogRoot).Read("service", catalogServiceId)!.Value.Bytes.SequenceEqual(pixel), "Catalog image persists across service restarts");
    Reject(() => catalog.Save("Cash", catalogServiceId, pixel), "Unsupported catalog kind rejected");
    Reject(() => catalog.Save("../service", catalogServiceId, pixel), "Catalog upload path traversal rejected");
    Reject(() => catalog.Save("", catalogServiceId, pixel), "Empty catalog kind rejected");
    Reject(() => catalog.Save("service", Guid.Empty, pixel), "Empty catalog id rejected");
    Reject(() => catalog.Read("../service", catalogServiceId), "Catalog read path traversal rejected");
    Reject(() => catalog.Read("service", Guid.Empty), "Catalog read with empty id rejected");
    Reject(() => catalog.Save("service", catalogServiceId, []), "Empty catalog upload rejected");
    Reject(() => catalog.Save("service", catalogServiceId, new byte[5_000_001]), "Catalog upload above 5 MB rejected");
    Reject(() => catalog.Save("service", catalogServiceId, System.Text.Encoding.UTF8.GetBytes("<svg>not a catalog image</svg>")), "Non-JPG/PNG catalog files rejected");
    Check(catalog.Read("service", catalogServiceId)!.Value.Bytes.SequenceEqual(pixel), "Rejected catalog upload keeps the previous image intact");
    catalog.Save("service", catalogServiceId, pixel);
    Check(Directory.GetFiles(Path.Combine(catalogRoot, "App_Data", "CatalogImages"), $"service-{catalogServiceId:N}.*").Length == 1, "Replacing a catalog picture leaves only the current image");
    catalog.Remove("service", sharedId);
    Check(catalog.Read("service", sharedId) == null && catalog.Read("addon", sharedId) != null, "Removing one catalog picture preserves the other kind");
    catalog.Remove("addon", sharedId);
    Check(catalog.GetImageUrl("addon", sharedId) == null, "Removed catalog picture clears its image URL");
    Check(catalog.GetImageUrl("service", catalogServiceId, "preview") == null && catalog.Read("service", catalogServiceId, "preview") == null, "Catalog preview starts empty without uploads");
    catalog.Save("service", catalogServiceId, pixel, "preview");
    var previewSaved = catalog.Read("service", catalogServiceId, "preview")!.Value;
    Check(previewSaved.ContentType == "image/png" && previewSaved.Bytes.SequenceEqual(pixel), "Uploaded preview picture can be read with correct content type");
    var cardUrl = catalog.GetImageUrl("service", catalogServiceId)!;
    var previewUrl = catalog.GetImageUrl("service", catalogServiceId, "preview")!;
    Check(previewUrl.Contains("&slot=preview") && !cardUrl.Contains("slot="), "Preview URL carries slot while card URL does not");
    var previewJpg = new byte[] { 255, 216, 255, 0 };
    catalog.Save("service", catalogServiceId, previewJpg, "preview");
    Check(catalog.Read("service", catalogServiceId)!.Value.Bytes.SequenceEqual(pixel) && catalog.Read("service", catalogServiceId, "preview")!.Value.Bytes.SequenceEqual(previewJpg), "Card and preview pictures coexist with independent bytes");
    Check(catalog.Read("service", catalogServiceId, "preview")!.Value.ContentType == "image/jpeg", "Preview JPG bytes round-trip with correct content type");
    catalog.Remove("service", catalogServiceId, "preview");
    Check(catalog.Read("service", catalogServiceId, "preview") == null && catalog.Read("service", catalogServiceId) != null, "Removing preview keeps card picture");
    catalog.Save("service", catalogServiceId, previewJpg, "preview");
    catalog.Remove("service", catalogServiceId);
    Check(catalog.Read("service", catalogServiceId) == null && catalog.Read("service", catalogServiceId, "preview") != null, "Removing card keeps preview picture");
    catalog.Save("service", catalogServiceId, pixel);
    Reject(() => catalog.Save("service", catalogServiceId, pixel, "cover"), "Unsupported preview slot rejected on save");
    Reject(() => catalog.Save("service", catalogServiceId, pixel, ""), "Empty preview slot rejected on save");
    Reject(() => catalog.Save("service", catalogServiceId, pixel, "../preview"), "Preview slot path traversal rejected on save");
    Reject(() => catalog.Read("service", catalogServiceId, "cover"), "Unsupported preview slot rejected on read");
    Reject(() => catalog.Read("service", catalogServiceId, ""), "Empty preview slot rejected on read");
    Reject(() => catalog.Read("service", catalogServiceId, "../preview"), "Preview slot path traversal rejected on read");
    Reject(() => catalog.GetImageUrl("service", catalogServiceId, "cover"), "Unsupported preview slot rejected on URL");
    Reject(() => catalog.GetImageUrl("service", catalogServiceId, ""), "Empty preview slot rejected on URL");
    Reject(() => catalog.GetImageUrl("service", catalogServiceId, "../preview"), "Preview slot path traversal rejected on URL");
    Reject(() => catalog.Save("service", catalogServiceId, new byte[5_000_001], "preview"), "Preview upload above 5 MB rejected");
    Reject(() => catalog.Save("service", catalogServiceId, System.Text.Encoding.UTF8.GetBytes("<svg>not a catalog image</svg>"), "preview"), "Non-JPG/PNG preview files rejected");
}
finally { if (Directory.Exists(catalogRoot)) Directory.Delete(catalogRoot, recursive: true); }
string? registrationError = RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "Female", "123 Sampaguita Street, Barangay Malinis", "Glow1234");
Check(registrationError == null, "Valid registration passes all rules");
Check(RegistrationRules.Validate("", "Santos", "maria@example.com", 25, "", "123 Sampaguita Street", "Glow1234") != null, "Blank first name rejected");
Check(RegistrationRules.Validate("Maria", "S4ntos", "maria@example.com", 25, "", "123 Sampaguita Street", "Glow1234") != null, "Last name with numbers rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "not-an-email", 25, "", "123 Sampaguita Street", "Glow1234") != null, "Invalid email rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 12, "", "123 Sampaguita Street", "Glow1234") != null, "Age below 13 rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 121, "", "123 Sampaguita Street", "Glow1234") != null, "Age above 120 rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "Unknown", "123 Sampaguita Street", "Glow1234") != null, "Gender outside the list rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "", "Short", "Glow1234") != null, "Address below 8 characters rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "", "123 Sampaguita Street", "short") != null, "Password below 8 characters rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "", "123 Sampaguita Street", "12345678") != null, "Password without letters rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "", "123 Sampaguita Street", "Password") != null, "Password without numbers rejected");
Check(RegistrationRules.Genders.Length == 3, "Gender options list is defined");
Console.WriteLine($"All {checks} workflow, availability, payment QR, catalog image, preview-slot and registration checks passed.");

public class PersistentQuoteProxy : System.Reflection.DispatchProxy
{
    public object?[]? Arguments { get; private set; }
    public List<AppUser> TestUsers { get; set; } = [];
    public List<Booking> TestBookings { get; set; } = [];
    public List<Payment> TestPayments { get; set; } = [];
    private readonly object gate = new();
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "get_IsPersistent") return true;
        if (method?.Name == "get_Gate") return gate;
        if (method?.Name == "get_Users") return TestUsers;
        if (method?.Name == "get_Bookings") return TestBookings;
        if (method?.Name == "get_Payments") return TestPayments;
        if (method?.Name != nameof(IPersistentStudioStore.SubmitBooking)) throw new InvalidOperationException("Unexpected repository call.");
        Arguments = args;
        return new Booking();
    }
}
