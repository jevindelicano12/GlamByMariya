using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;

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
PreparedPersistentBooking.ValidateClaim(quote.RequiredDownPayment, quote);
Check(true, "the exact required down payment is accepted");
Reject(() => PreparedPersistentBooking.ValidateClaim(750m, quote), "an amount above the exact down payment is denied");
Reject(() => PreparedPersistentBooking.ValidateClaim(quote.FinalTotal, quote), "paying the full total online is denied (exact down payment only)");
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

Console.WriteLine($"{checks} isolated checks passed. No database, owner-scoped payment, or live workflow exercised.");
