using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GlamByMariya.Core.Entities;

const string local = "Host=127.0.0.1;Port=5432;Database=glam_auth_mvc_workflow_test;Username=booking_test_runtime";
var count = 0;
void Check(bool result, string label)
{
    if (!result) throw new InvalidOperationException("FAIL: " + label);
    count++;
    Console.WriteLine("PASS: " + label);
}
void Reject(Action action, string label)
{
    try { action(); } catch (InvalidOperationException) { Check(true, label); return; }
    throw new InvalidOperationException("Expected rejection: " + label);
}
void RejectProtected(Action action, string label)
{
    try { action(); }
    catch (InvalidOperationException e) when (e.Message == "Protected test booking database operations are not implemented.") { Check(true, label); return; }
    throw new InvalidOperationException("Expected protected-boundary rejection: " + label);
}

Reject(() => BookingTestFixture.ValidateTarget(false, local, "booking_test"), "disabled by default");
BookingTestFixture.ValidateTarget(true, local, "booking_test");
Check(true, "explicit test target accepted as configuration only");
foreach (var bad in new[] {
    "Host=localhost;Port=5432;Database=glam_auth_mvc_workflow_test;Username=booking_test_runtime",
    "Host=example.test;Port=5432;Database=glam_auth_mvc_workflow_test;Username=booking_test_runtime",
    "Host=127.0.0.1;Port=5433;Database=glam_auth_mvc_workflow_test;Username=booking_test_runtime",
    "Host=127.0.0.1;Port=5432;Database=postgres;Username=booking_test_runtime",
    "Host=127.0.0.1;Port=5432;Database=glam_auth_mvc_workflow_test;Username=postgres",
    "Host=127.0.0.1;Port=5432;Database=glam_auth_mvc_workflow_test;Username=other",
    "not a connection string" })
    Reject(() => BookingTestFixture.ValidateTarget(true, bad, "booking_test"), "incorrect target rejected");
Reject(() => BookingTestFixture.ValidateTarget(true, local, "public"), "public schema rejected");
Reject(() => BookingTestFixture.ValidateTarget(true, local, "booking_test;DROP SCHEMA public"), "injected schema rejected");
Reject(() => BookingTestFixture.ValidateTableNames(["fixture_runs", "artists"]), "missing fixture table rejected");
BookingTestFixture.ValidateTableNames(["fixture_runs", "artists", "conflict_reservations"]);
Check(true, "complete fixture table-name set accepted as synthetic metadata");

var disabledServices = new ServiceCollection();
Reject(() => BookingTestFixture.AddOfflineMetadata(disabledServices, false, local, "booking_test"), "disabled fixture not registered");
Check(!disabledServices.Any(x => x.ServiceType == typeof(ConflictFixtureContext)), "disabled DI has no fixture context");
var services = new ServiceCollection();
BookingTestFixture.AddOfflineMetadata(services, true, local, "booking_test");
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var context = scope.ServiceProvider.GetRequiredService<ConflictFixtureContext>();
// No provider is registered by this fixture; calling database APIs must fail.
Reject(() => { _ = context.Database.ProviderName; }, "metadata fixture has no database provider");
// Npgsql supplies model services only in a separate in-memory metadata context;
// UseNpgsql creates options but no connection is opened or SQL executed.
var options = new DbContextOptionsBuilder<ConflictFixtureContext>().UseNpgsql(local).Options;
using var modelContext = new ConflictFixtureContext(options);
var entity = modelContext.Model.FindEntityType(typeof(ConflictReservation))!;
Check(entity.GetSchema() == "booking_test" && entity.GetTableName() == "conflict_reservations", "EF maps reservation to test schema only");
Check(entity.FindProperty(nameof(ConflictReservation.ReservedUntil))!.GetColumnName() == "reserved_until", "EF maps saved buffer boundary");
Check(entity.GetForeignKeys().Count() == 2 && entity.GetForeignKeys().All(x => x.DeleteBehavior == DeleteBehavior.Restrict), "EF maps fixture and artist foreign keys as restrict");
var customerId = Guid.NewGuid();
var ephemeralPasswordA = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
var ephemeralPasswordB = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
var connectionA = $"Host=127.0.0.1;Port=5432;Database=glam_auth_mvc_workflow_test;Username=booking_test_customer_a;Password={ephemeralPasswordA};Pooling=true";
var connectionB = $"Host=127.0.0.1;Port=5432;Database=glam_auth_mvc_workflow_test;Username=booking_test_customer_b;Password={ephemeralPasswordB};Pooling=true";
Reject(() => new SyntheticCustomerConnections(false, connectionA, connectionB, "booking_test"), "customer connections disabled by default");
Reject(() => new SyntheticCustomerConnections(true, connectionA.Replace("127.0.0.1", "example.test"), connectionB, "booking_test"), "customer connections reject remote host");
Reject(() => new SyntheticCustomerConnections(true, connectionA, connectionA, "booking_test"), "shared credential fallback rejected");
Reject(() => new SyntheticCustomerConnections(true, connectionA, connectionB.Replace(ephemeralPasswordB, ephemeralPasswordA), "booking_test"), "shared Customer secret rejected");
Reject(() => new SyntheticCustomerConnections(true, connectionB, connectionA, "booking_test"), "swapped Customer logins rejected");
Reject(() => new SyntheticCustomerConnections(true, connectionA.Replace($"Password={ephemeralPasswordA};", ""), connectionB, "booking_test"), "missing Customer secret rejected");
Reject(() => new SyntheticCustomerConnections(true, connectionA, connectionB.Replace("127.0.0.1", "example.test"), "booking_test"), "second Customer remote host rejected");
Reject(() => new SyntheticCustomerConnections(true, connectionA.Replace("booking_test_customer_a", "booking_test_runtime"), connectionB, "booking_test"), "shared runtime login rejected");
Reject(() => new SyntheticCustomerConnections(true, connectionA, connectionB, "public"), "customer connections reject wrong schema");
var customerConnections = new SyntheticCustomerConnections(true, connectionA, connectionB, "booking_test");
_ = new BookingTestReadRepository(customerConnections);
Check(true, "explicit local read repository construction does not connect");
var serviceId = Guid.NewGuid(); var addonId = Guid.NewGuid();
var validRequest = new BookingTestCreateRequest(Guid.NewGuid(), serviceId, [addonId], 1, "auto",
    DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(10).AddHours(2), DateTimeKind.Utc),
    "Synthetic address", "Synthetic area", 150m, 75m, true, true);
var serviceItem = new CatalogItem { Id=serviceId, Name="Synthetic service", Price=100m, Minutes=60 };
var addonItem = new CatalogItem { Id=addonId, Name="Synthetic addon", Price=50m, Minutes=15 };
var requestHash = BookingTestCreateRepository.ValidateAndHash(customerId, validRequest);
Check(BookingTestCreateRepository.ValidateAndQuote(validRequest, serviceItem, [addonItem], 50).FinalTotal == 150m,
    "server catalog determines test quote");
var groupService = new CatalogItem { Id=serviceId, Name="Synthetic service", Price=100m, GroupPrice=80m, GroupMinimumPeople=4, Minutes=60 };
var groupRequest = validRequest with { People=4, Package="group", AddonIds=[addonId, addonId], ExpectedTotal=420m, ExpectedMinimum=210m };
var groupQuote = BookingTestCreateRepository.ValidateAndQuote(groupRequest, groupService, [addonItem, addonItem], 50);
Check(groupQuote.FinalTotal == 420m && groupQuote.RequiredDownPayment == 210m && groupQuote.DurationMinutes == 270,
    "isolated group quote includes each selected add-on and existing package rate");
Check(BookingTestCreateRepository.ValidateAndHash(customerId, groupRequest).Length == 32, "isolated group request accepts per-person add-on quantities");
Reject(() => BookingTestCreateRepository.ValidateAndQuote(groupRequest, groupService, [addonItem], 50), "group quote rejects missing quantity snapshot");
Reject(() => BookingTestCreateRepository.ValidateAndHash(customerId, groupRequest with { AddonIds=[addonId, addonId, addonId, addonId, addonId] }), "group request rejects add-on quantity beyond people");
Check(BookingTestCreateRepository.SameReplay(requestHash, BookingTestCreateRepository.ValidateAndHash(customerId, validRequest)), "identical retry has same fingerprint");
Check(!BookingTestCreateRepository.SameReplay(requestHash, BookingTestCreateRepository.ValidateAndHash(customerId, validRequest with { Address="Different synthetic address" })), "different replay payload is detected");
Check(BookingTestCreateRepository.SameReplay(requestHash, BookingTestCreateRepository.ValidateAndHash(customerId, validRequest with { ExpectedTotal=150.00m, ExpectedMinimum=75.00m })), "equivalent monetary scales have the same replay fingerprint");
Check(!BookingTestCreateRepository.SameReplay(requestHash, BookingTestCreateRepository.ValidateAndHash(Guid.NewGuid(), validRequest)), "fingerprint binds actor");
Check(typeof(BookingTestCreateRequest).GetProperty("CustomerId") == null && typeof(BookingTestCreateRequest).GetProperty("Role") == null, "request has no ownership or role override fields");
Check(typeof(BookingTestCreateRequest).GetProperty("Status") == null, "request cannot select an invalid or pre-confirmed status");
Reject(() => BookingTestCreateRepository.ValidateAndHash(Guid.Empty, validRequest), "missing authenticated actor rejected");
Reject(() => BookingTestCreateRepository.ValidateAndHash(customerId, validRequest with { SubmissionId=Guid.Empty }), "missing submission token rejected");
Reject(() => BookingTestCreateRepository.ValidateAndHash(customerId, validRequest with { ServiceId=Guid.Empty }), "missing service rejected");
Reject(() => BookingTestCreateRepository.ValidateAndHash(customerId, validRequest with { ExpectedTotal=0m }), "missing quote rejected");
Reject(() => BookingTestCreateRepository.ValidateAndHash(customerId, validRequest with { AddonIds=[addonId, addonId] }), "add-on quantity above party rejected");
Reject(() => BookingTestCreateRepository.ValidateAndHash(customerId, validRequest with { TransportationAccepted=false }), "missing transportation consent rejected");
Reject(() => BookingTestCreateRepository.ValidateAndQuote(validRequest with { ExpectedTotal=151m }, serviceItem, [addonItem], 50), "changed client total rejected against server quote");
Reject(() => BookingTestCreateRepository.ValidateAndQuote(validRequest, serviceItem, [], 50), "missing catalog add-on rejected");
Reject(() => BookingTestCreateRepository.ValidateAndQuote(validRequest, new CatalogItem { Id=Guid.NewGuid(), Name="wrong", Price=100m, Minutes=60 }, [addonItem], 50), "wrong catalog service rejected");
var createRepo = new BookingTestCreateRepository(customerConnections);
Reject(() => createRepo.CreateAsync(null!, validRequest).GetAwaiter().GetResult(), "missing verified Customer rejected before connection");
var readRepo = new BookingTestReadRepository(customerConnections);
Reject(() => readRepo.FindOwnAsync(null!, Guid.NewGuid()).GetAwaiter().GetResult(), "missing verified Customer cannot retrieve before connection");
Check(typeof(BookingTestCreateRepository).GetMethod("CreateAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetParameters()[0].ParameterType == typeof(SyntheticBookingSessions.VerifiedCustomer), "create repository requires verified synthetic Customer");
Check(typeof(BookingTestReadRepository).GetMethod("FindOwnAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetParameters()[0].ParameterType == typeof(SyntheticBookingSessions.VerifiedCustomer), "read repository requires verified synthetic Customer");
Check(true, "create repository construction did not connect");
// The following tests exercise only a controlled in-memory double, not the
// Npgsql repository, PostgreSQL constraints or an actual transaction rollback.
var fake = new FakeBookingTransactions();
var fakeStart = validRequest.StartUtc;
var fakeEnd = fakeStart.AddMinutes(90);
var fakeId = fake.Create(customerId, validRequest.SubmissionId, requestHash, fakeStart, fakeEnd);
Check(fake.Create(customerId, validRequest.SubmissionId, requestHash, fakeStart, fakeEnd) == fakeId && fake.Count == 1, "FAKE identical retry returns one booking");
Reject(() => fake.Create(customerId, validRequest.SubmissionId, new byte[32], fakeStart, fakeEnd), "FAKE conflicting replay rejected");
Reject(() => fake.Create(customerId, Guid.NewGuid(), requestHash, fakeStart.AddMinutes(89), fakeEnd.AddMinutes(89)), "FAKE buffer interval conflicts");
Reject(() => fake.Create(customerId, Guid.NewGuid(), requestHash, fakeEnd, fakeEnd.AddHours(1), true), "FAKE simulated failure before commit");
Check(fake.Count == 1, "FAKE failed transaction leaves no additional record");
var raceFake = new FakeBookingTransactions();
using (var barrier = new Barrier(2))
{
    var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(() => {
        barrier.SignalAndWait();
        try { raceFake.Create(customerId, Guid.NewGuid(), requestHash, fakeStart, fakeEnd); return true; }
        catch (InvalidOperationException) { return false; }
    })).ToArray();
    Task.WaitAll(attempts);
    Check(attempts.Count(x => x.Result) == 1 && raceFake.Count == 1, "FAKE concurrent conflicting requests have one winner");
}
// Separate synthetic test-host sessions; not connected to the repositories or DB.
var clock = new SyntheticClock();
var sessions = new SyntheticBookingSessions(clock);
Reject(() => sessions.ProvisionCredential(Guid.NewGuid()), "synthetic provisioning rejects non-fixture identities");
var credentialA = sessions.ProvisionCredential(SyntheticBookingSessions.CustomerA);
var credentialB = sessions.ProvisionCredential(SyntheticBookingSessions.CustomerB);
Reject(() => sessions.ProvisionCredential(SyntheticBookingSessions.CustomerA), "synthetic credential cannot be reprovisioned");
Reject(() => sessions.SignIn(SyntheticBookingSessions.CustomerA.ToString()), "Customer ID is not a synthetic login credential");
Reject(() => sessions.SignIn(new string('0', 64)), "unknown synthetic credential rejected");
var syntheticCanary = new string('C', 64);
var priorOutput = Console.Out;
using (var captured = new StringWriter())
{
    string? denial = null;
    try
    {
        Console.SetOut(captured);
        try { sessions.SignIn(syntheticCanary); }
        catch (InvalidOperationException e) { denial = e.Message; }
    }
    finally { Console.SetOut(priorOutput); }
    Check(denial != null && !denial.Contains(syntheticCanary, StringComparison.Ordinal) &&
          !captured.ToString().Contains(syntheticCanary, StringComparison.Ordinal),
          "offline invalid synthetic credential omitted from observed exception and console output");
}
var tokenA = sessions.SignIn(credentialA);
var tokenB = sessions.SignIn(credentialB);
Check(sessions.Authenticate(tokenA) == SyntheticBookingSessions.CustomerA && sessions.Authenticate(tokenB) == SyntheticBookingSessions.CustomerB, "server-issued sessions bind distinct fixture Customers");
Check(typeof(SyntheticBookingSessions.VerifiedCustomer).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).All(x => x.IsPrivate), "verified synthetic identity constructors are private");
Reject(() => SyntheticBookingSessions.VerifiedCustomer.FromSession(sessions, new string('0', 64)), "synthetic identity factory verifies the session");
var verifiedA = sessions.Verify(tokenA);
RejectProtected(() => createRepo.CreateAsync(verifiedA, validRequest).GetAwaiter().GetResult(), "protected PostgreSQL create fails closed before connection");
RejectProtected(() => readRepo.FindOwnAsync(verifiedA, Guid.NewGuid()).GetAwaiter().GetResult(), "protected PostgreSQL read fails closed before connection");
var temporaryProof = verifiedA.CopyProof();
Check(temporaryProof.Length == 32, "verified synthetic session exposes fixed-size proof to trusted test transport");
System.Security.Cryptography.CryptographicOperations.ZeroMemory(temporaryProof);
Check(temporaryProof.All(b => b == 0), "temporary synthetic proof can be cleared");
var verifiedB = sessions.Verify(tokenB);
Check(customerConnections.ExpectedLogin(verifiedA) == "booking_test_customer_a" && customerConnections.ExpectedLogin(verifiedB) == "booking_test_customer_b", "verified Customer selects distinct PostgreSQL login");
Check(customerConnections.PoolingDisabled, "pooling disabled for both synthetic logins");
RejectProtected(() => customerConnections.Create(verifiedA), "connection factory fails closed");
RejectProtected(() => BookingTestDatabaseSessionRepository.IssueAsync(null!, null!, verifiedA, CancellationToken.None).GetAwaiter().GetResult(), "issuer helper fails closed");
RejectProtected(() => BookingTestDatabaseSessionRepository.LockAsync(null!, null!, verifiedA, CancellationToken.None).GetAwaiter().GetResult(), "session lock helper fails closed");
RejectProtected(() => BookingTestDatabaseSessionRepository.RevokeAsync(null!, null!, verifiedA, CancellationToken.None).GetAwaiter().GetResult(), "revocation helper fails closed");
RejectProtected(() => BookingTestCreateRepository.EnsureCustomerAsync(null!, null!, verifiedA, CancellationToken.None).GetAwaiter().GetResult(), "customer lookup helper fails closed");
var application = new SyntheticBookingApplication(sessions, new FakeSyntheticBookingStore());
var postgresApplication = new SyntheticBookingApplication(sessions, new PostgresSyntheticBookingStore(createRepo, readRepo));
Reject(() => postgresApplication.FindOwnAsync(new string('0', 64), Guid.NewGuid()).GetAwaiter().GetResult(), "PostgreSQL adapter rejects invalid session before connection");
var ownedBooking = application.CreateAsync(tokenA, validRequest).GetAwaiter().GetResult();
Check(application.FindOwnAsync(tokenA, ownedBooking).GetAwaiter().GetResult() == ownedBooking, "FAKE session-bound owner can retrieve booking");
Check(application.FindOwnAsync(tokenB, ownedBooking).GetAwaiter().GetResult() == null, "FAKE another Customer cannot retrieve booking");
Check(application.CreateAsync(tokenA, validRequest).GetAwaiter().GetResult() == ownedBooking, "FAKE session-bound identical replay returns booking");
Reject(() => application.CreateAsync(tokenA, validRequest with { Address = "Changed synthetic address" }).GetAwaiter().GetResult(), "FAKE session-bound changed replay rejected");
Reject(() => application.FindOwnAsync(new string('0', 64), ownedBooking).GetAwaiter().GetResult(), "FAKE invalid session cannot access booking");
Reject(() => sessions.Authenticate(tokenA[..^1] + (tokenA[^1] == 'A' ? 'B' : 'A')), "modified synthetic token rejected");
Reject(() => new SyntheticBookingSessions(clock).Authenticate(tokenA), "synthetic token from another host rejected");
sessions.Disable(SyntheticBookingSessions.CustomerA);
RejectProtected(() => customerConnections.Create(verifiedA), "connection factory remains closed after revocation");
Reject(() => postgresApplication.CreateAsync(tokenA, validRequest).GetAwaiter().GetResult(), "PostgreSQL adapter rejects revoked session before connection");
Reject(() => _ = verifiedA.CustomerId, "verified synthetic identity rechecks revocation");
Reject(() => verifiedA.CopyProof(), "revoked synthetic session cannot export proof");
Reject(() => application.FindOwnAsync(tokenA, ownedBooking).GetAwaiter().GetResult(), "FAKE revoked session cannot retrieve booking");
Reject(() => application.CreateAsync(tokenA, validRequest).GetAwaiter().GetResult(), "FAKE revoked session cannot create booking");
Reject(() => sessions.Authenticate(tokenA), "disabled synthetic Customer session revoked");
Reject(() => sessions.SignIn(credentialA), "disabled synthetic Customer cannot sign in");
Check(sessions.Authenticate(tokenB) == SyntheticBookingSessions.CustomerB, "disabling one synthetic Customer does not disable another");
clock.Advance(TimeSpan.FromMinutes(10));
Reject(() => sessions.Authenticate(tokenB), "synthetic session expires at deadline");
// Controlled in-process ordering test; no repository transaction or PostgreSQL.
var orderingSessions = new SyntheticBookingSessions(new SyntheticClock());
var orderingA = orderingSessions.SignIn(orderingSessions.ProvisionCredential(SyntheticBookingSessions.CustomerA));
var orderingB = orderingSessions.SignIn(orderingSessions.ProvisionCredential(SyntheticBookingSessions.CustomerB));
var operationLease = orderingSessions.Verify(orderingA).AcquireOperationAsync().GetAwaiter().GetResult();
using (orderingSessions.Verify(orderingB).AcquireOperationAsync().GetAwaiter().GetResult())
    Check(true, "different synthetic Customer can hold an independent operation lease");
var disabling = orderingSessions.DisableAsync(SyntheticBookingSessions.CustomerA);
Check(!disabling.IsCompleted, "synthetic revocation waits for the active operation lease");
operationLease.Dispose();
disabling.GetAwaiter().GetResult();
Reject(() => orderingSessions.Verify(orderingA).AcquireOperationAsync().GetAwaiter().GetResult(), "synthetic operation rejected after revocation completes");
operationLease.Dispose();
Check(true, "synthetic operation lease disposal is idempotent");
Console.WriteLine($"Offline isolated fixture checks: {count} passed.");
Console.WriteLine("No PostgreSQL connection, live schema detection, database retrieval, booking creation, payment or restart persistence was exercised.");

sealed class SyntheticClock : TimeProvider
{
    private DateTimeOffset now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}
