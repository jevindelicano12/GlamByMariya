using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using System.Security.Claims;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}

// Actual pure pricing and validation code, with synthetic catalog data.
var service = new CatalogItem { Name = "Synthetic", Price = 1000.01m, Minutes = 60 };
var quote = BookingPricing.Calculate(service, [], 1, 0m, downPaymentPercent: 50);
Check(quote.FinalTotal == 1000.01m && quote.RequiredDownPayment == 500.01m, "server quote and peso midpoint rounding");
foreach (var amount in new[] { quote.RequiredDownPayment, 750m, quote.FinalTotal })
{
    PreparedPersistentBooking.ValidateClaim(amount, quote);
    Check(true, "minimum, intermediate and full claim accepted");
}
Reject(() => PreparedPersistentBooking.ValidateClaim(500m, quote), "below minimum denied");
Reject(() => PreparedPersistentBooking.ValidateClaim(1000.02m, quote), "above total denied");
Reject(() => PreparedPersistentBooking.ValidateClaim(500.011m, quote), "fractional cent denied");
BookingService.ValidateExpectedQuote(quote, quote.FinalTotal, quote.RequiredDownPayment);
Check(true, "matching quote accepted");
Reject(() => BookingService.ValidateExpectedQuote(quote, quote.FinalTotal, 500m), "stale minimum denied");
Reject(() => BookingService.ValidateExpectedQuote(quote, 999m, quote.RequiredDownPayment), "stale total denied");
Reject(() => BookingService.ValidateExpectedQuote(quote, quote.FinalTotal, null), "incomplete quote denied");
Check(!PreparedPersistentBooking.Enabled, "persistent booking gate remains disabled");
BookingService.ValidatePersistentCorrectionToken(5, "42");
Check(true, "syntactically valid persistent correction token");
Reject(() => BookingService.ValidatePersistentCorrectionToken(null, "42"), "missing correction revision denied");
Reject(() => BookingService.ValidatePersistentCorrectionToken(-1, "42"), "negative correction revision denied");
Reject(() => BookingService.ValidatePersistentCorrectionToken(5, null), "missing correction version denied");
Reject(() => BookingService.ValidatePersistentCorrectionToken(5, "malformed"), "malformed correction version denied");
Reject(() => BookingService.ValidatePersistentCorrectionToken(5, "4294967296"), "out-of-range correction version denied");

var payment = new Payment { Id = Guid.NewGuid(), ProofKey = "synthetic", PersistentReviewRevision = 5, PersistentReviewVersion = "42" };
BookingService.ValidatePersistentPaymentReview(payment, payment.Id, 5, "42", false);
Check(true, "matching persistent review evidence accepted");
Reject(() => BookingService.ValidatePersistentPaymentReview(payment, payment.Id, 4, "42", false), "stale revision denied");
Reject(() => BookingService.ValidatePersistentPaymentReview(payment, payment.Id, 5, "41", false), "stale version denied");
Reject(() => BookingService.ValidatePersistentPaymentReview(payment, Guid.NewGuid(), 5, "42", false), "wrong payment denied");
payment.ProofKey = null;
Reject(() => BookingService.ValidatePersistentPaymentReview(payment, payment.Id, 5, "42", false), "missing proof denied");

// Guard tests use a synthetic provider only: they do not issue or validate a real cookie.
var account = Guid.NewGuid();
var subject = Guid.NewGuid();
var snapshot = new AccountSecuritySnapshot(account, true, false, "Customer",
    SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase, 1, account, account,
    SessionIdentityOwnerKind.Customer, true, AuthenticationIdentityPolicy.Supabase, subject,
    SessionCredentialState.Ready, 1, new string('A', 64));
var evidence = new AccountSessionEvidence(account, "Customer", "1", new string('A', 64));
async Task<bool> Eligible(AccountSecuritySnapshot s, AccountSessionEvidence e) =>
    (await new AccountSessionSecurityGuard(new SyntheticProvider(s)).EvaluateAsync(e)).IsEligible;
Check(await Eligible(snapshot, evidence), "eligible synthetic Customer snapshot");
Check(!await Eligible(snapshot, evidence with { Role = "Admin" }), "client role mismatch denied");
Check(!await Eligible(snapshot with { IsActive = false }, evidence), "inactive account denied");
Check(!await Eligible(snapshot with { SupabaseSubject = null }, evidence), "missing provider subject denied");
Check(!await Eligible(snapshot with { IdentityOwnerAccountId = Guid.NewGuid() }, evidence), "mismatched identity owner denied");

// The issuer is a synthetic test principal, not a trusted host or provider.
var versioned = new SyntheticVersionedProvider(new VersionedAccountSecurity(snapshot, 7));
var authorizer = new VersionedSessionAuthorization(versioned);
ClaimsPrincipal Ticket(Guid id, string role, Guid providerSubject, string generation) => new(new ClaimsIdentity(
    [new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Role, role),
     new(DurableSessionAuthorization.EpochClaim, "1"), new("account_version", new string('A', 64)),
     new(DurableSessionAuthorization.SubjectClaim, providerSubject.ToString()),
     new(VersionedSessionAuthorization.SecurityVersionClaim, generation)], "synthetic"));
var ticket = Ticket(account, "Customer", subject, "7");
Check(await authorizer.ValidateCookie(ticket), "synthetic current session accepted by existing guard");
Check(!await authorizer.ValidateCookie(Ticket(account, "Admin", subject, "7")), "synthetic forged staff role denied");
Check(!await authorizer.ValidateCookie(Ticket(account, "Customer", Guid.NewGuid(), "7")), "synthetic wrong subject denied");
Check(!await authorizer.ValidateCookie(Ticket(Guid.NewGuid(), "Customer", subject, "7")), "synthetic wrong account denied");
versioned.Current = versioned.Current with { SecurityVersion = 8 };
Check(!await authorizer.ValidateCookie(ticket), "synthetic session denied after generation advance");
Check(!await authorizer.ValidateCookie(ticket), "synthetic revoked session remains denied on repeated check");
Check(await authorizer.ValidateCookie(Ticket(account, "Customer", subject, "8")), "synthetic new generation accepted");
versioned.Current = versioned.Current with { Account = snapshot with { CredentialState = SessionCredentialState.Pending } };
Check(!await authorizer.ValidateCookie(Ticket(account, "Customer", subject, "8")), "synthetic pending credential denied");
Console.WriteLine($"{checks} isolated checks passed. No provider, database, cookie, owner-scoped payment, or live workflow exercised.");

sealed class SyntheticProvider(AccountSecuritySnapshot snapshot) : IAccountSecuritySnapshotProvider
{
    public Task<AccountSecuritySnapshot?> LoadAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<AccountSecuritySnapshot?>(snapshot);
}

sealed class SyntheticVersionedProvider(VersionedAccountSecurity current) : IVersionedAccountSecurityProvider
{
    public VersionedAccountSecurity Current { get; set; } = current;
    public Task<VersionedAccountSecurity?> LoadAsync(Guid accountId, CancellationToken ct) =>
        Task.FromResult<VersionedAccountSecurity?>(Current);
}
