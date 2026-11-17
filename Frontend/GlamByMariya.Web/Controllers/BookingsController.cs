using System.Security.Claims;
using System.Text.Json;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace GlamByMariya.Web.Controllers;
[Authorize(Roles = "Customer")]
public class BookingsController(IStudioStore store, BookingService workflow, IWebHostEnvironment environment, PaymentQrService paymentQr, CatalogImageService imageService, IAppFileStore files) : Controller
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        workflow.ExpireOverduePayments();
        base.OnActionExecuting(context);
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult MixedSelection()
    {
        if (store.IsPersistent) return NotFound();
        lock (store.Gate)
        {
            ViewBag.Services = store.Services.Where(s => s.Active && !s.Archived).ToList();
            ViewBag.AddOns = store.AddOns.Where(a => a.Active && !a.Archived).ToList();
            return View();
        }
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(32768)]
    public IActionResult MixedQuote([FromBody] MixedPersonSelection[] people)
    {
        if (store.IsPersistent) return NotFound();
        if (!ModelState.IsValid || people == null) return BadRequest(new { error = "Choose a valid service and add-ons for each person." });
        try { return Json(workflow.QuoteMixed(people)); }
        catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(32768), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult MixedAvailability([FromBody] MixedAvailabilityRequest request)
    {
        if (store.IsPersistent) return NotFound();
        if (!ModelState.IsValid || request?.People == null || !DateTime.TryParseExact(request.Month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var month))
            return BadRequest(new { error = "Choose a valid month and services for every person." });
        lock (store.Gate)
        {
            try { return Json(workflow.GetAvailability(month, workflow.QuoteMixed(request.People).DurationMinutes, cancellationToken: HttpContext.RequestAborted)); }
            catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        }
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(32768), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult MixedAlternatives([FromBody] MixedAlternativesRequest request)
    {
        if (store.IsPersistent) return NotFound();
        if (!ModelState.IsValid || request?.People == null || !DateTime.TryParseExact(request.After, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            return BadRequest(new { error = "Choose a valid date and services for every person." });
        try { return Json(workflow.FindMixedAlternatives(request.People, date, 30)); }
        catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(32768), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult MixedValidateSlot([FromBody] MixedSlotRequest request)
    {
        if (store.IsPersistent) return NotFound();
        if (!ModelState.IsValid || request?.People == null || !DateTime.TryParseExact(request.Start, "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start))
            return BadRequest(new { error = "Choose a valid date, time and services for every person." });
        lock (store.Gate)
        {
            try
            {
                var quote = workflow.QuoteMixed(request.People, start);
                var calendar = workflow.GetAvailability(start, quote.DurationMinutes, cancellationToken: HttpContext.RequestAborted);
                var slot = calendar.Days.SelectMany(day => day.Slots).FirstOrDefault(candidate => candidate.Start == request.Start);
                if (slot == null || !slot.Available) return Json(new { available = false, message = slot?.Reason ?? "This time is outside the available booking slots." });
                workflow.CheckSlot(start, quote.DurationMinutes);
                return Json(new { available = true, quote, bufferMinutes = calendar.BufferMinutes, message = "Availability is based on makeup duration, preparation time and existing appointments. Travel feasibility has not yet been verified." });
            }
            catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        }
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Quote(Guid serviceId, Guid[] addOnIds, int people = 1, string package = "auto", DateTime? start = null)
    {
        if (!ModelState.IsValid) return BadRequest(new { error = "Enter a valid party size and package." });
        try { return Json(workflow.Quote(serviceId, addOnIds, people, package, start)); }
        catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Availability(string month, Guid serviceId, Guid[] addOnIds, Guid? bookingId, int people = 1, string package = "auto")
    {
        if (!DateTime.TryParseExact(month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)) return BadRequest(new { error = "Choose a valid month." });
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid availability request." });
        lock (store.Gate)
        {
            try
            {
                var selection = AvailabilitySelection(serviceId, addOnIds, bookingId, people, package);
                if (selection == null) return NotFound(new { error = "This appointment cannot be rescheduled." });
                return Json(workflow.GetAvailability(date, selection.Value.Duration, selection.Value.Exclude, HttpContext.RequestAborted));
            }
            catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        }
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult ValidateSlot(string start, Guid serviceId, Guid[] addOnIds, Guid? bookingId, int people = 1, string package = "auto")
    {
        if (!DateTime.TryParseExact(start, "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) || !ModelState.IsValid) return BadRequest(new { error = "Choose a valid date and time." });
        lock (store.Gate)
        {
            try
            {
                var selection = AvailabilitySelection(serviceId, addOnIds, bookingId, people, package);
                if (selection == null) return NotFound(new { error = "This appointment cannot be rescheduled." });
                workflow.CheckSlot(date, selection.Value.Duration, selection.Value.Exclude);
                 return Json(new { available = true, message = "Available for request at this moment; service and preparation time only. Travel is not estimated. Confirmation requires payment review and approval." });
            }
            catch (InvalidOperationException e) { return Json(new { available = false, message = e.Message }); }
        }
    }
    private (int Duration, Guid? Exclude)? AvailabilitySelection(Guid serviceId, Guid[] addOnIds, Guid? bookingId, int people, string package)
    {
        if (!bookingId.HasValue) return (workflow.SelectedDuration(serviceId, addOnIds, people, package), null);
        var b = store.Bookings.FirstOrDefault(b => b.Id == bookingId && b.CustomerId == UserId && b.Status == "Confirmed" && b.Start > store.Now);
        return b == null ? null : ((int)(b.End - b.Start).TotalMinutes, b.Id);
    }
    public IActionResult Index()
    {
        lock (store.Gate)
        {
            var preferences = HttpContext.RequestServices.GetRequiredService<AccountSettingsService>().Get(UserId);
            ViewBag.Notifications = preferences.NotificationsEnabled
                ? store.Notifications.Where(n => n.UserId == UserId).OrderByDescending(n => n.Created).Take(6).ToList()
                : new List<GlamByMariya.Core.Entities.Notification>();
            ViewBag.Payments = store.Payments.ToList();
            return View(store.Bookings.Where(b => b.CustomerId == UserId).OrderByDescending(b => b.Start).ToList());
        }
    }
    public IActionResult Create(Guid? serviceId)
    {
        ViewBag.PaymentQr = paymentQr.GetOptions();
        ViewBag.CatalogImages = (Func<string, Guid, string?>)((kind, id) => imageService.GetImageUrl(kind, id));
        ViewBag.CatalogPreviewImages = (Func<string, Guid, string?>)((kind, id) => imageService.GetImageUrl(kind, id, "preview"));
        lock (store.Gate)
        {
            ViewBag.Services = store.Services.Where(x => x.Active && !x.Archived).ToList();
            ViewBag.AddOns = store.AddOns.Where(x => x.Active && !x.Archived).ToList();
            ViewBag.Settings = store.Settings;
            ViewBag.SelectedService = serviceId;
            ViewBag.AccountAddress = store.Users.FirstOrDefault(u => u.Id == UserId)?.Address ?? "";
            return View();
        }
    }
    [HttpPost, RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> Create(Guid serviceId, Guid[] addOnIds, DateTime start, string address, string area, decimal deposit, string method, string reference, IFormFile? proof, bool agreed, int people = 1, string package = "auto", bool travelAcknowledged = false, decimal? expectedTotal = null, decimal? expectedMinimum = null)
    {
        string? path = null;
        try
        {
            if (!ModelState.IsValid) throw new InvalidOperationException("Check the number of people, date and payment amount.");
            if (!expectedTotal.HasValue || !expectedMinimum.HasValue) throw new InvalidOperationException("Review the current price before submitting.");
            if (method == "Cash") throw new InvalidOperationException("Deposits must use GCash, Maya or Bank Transfer.");
            if (proof == null || proof.Length == 0) throw new InvalidOperationException("Upload a payment screenshot to submit your booking.");
            path = await SaveProof(proof);
            workflow.Create(UserId, serviceId, addOnIds, start, address, area, deposit, method, reference, path, agreed, people, package, travelAcknowledged, expectedTotal, expectedMinimum);
            CompleteStaging(path);
            path = null;
            TempData["Success"] = "Your booking is submitted. The artist will verify your deposit and confirm your appointment.";
            return RedirectToAction("Index");
        }
        catch (InvalidOperationException e) { CleanupFailedStaging(path); TempData["Error"] = e.Message; return RedirectToAction("Create", new { serviceId }); }
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> CreateMixed(DateTime mixedStart, string address, string area, decimal deposit, string method, string reference, IFormFile? proof, bool agreed, bool travelAcknowledged, decimal? expectedTotal, decimal? expectedMinimum, string? mixedSelection, Guid submissionId, string? policyVersion, string? travelReminderVersion)
    {
        if (store.IsPersistent) return NotFound();
        string? path = null;
        try
        {
            if (!ModelState.IsValid) throw new InvalidOperationException("Check the date, payment amount and booking details.");
            if (submissionId == Guid.Empty) throw new InvalidOperationException("Reload the booking form before submitting.");
            if (!expectedTotal.HasValue || !expectedMinimum.HasValue) throw new InvalidOperationException("Review the current mixed-service price before submitting.");
            if (mixedStart == default) throw new InvalidOperationException("Choose an available makeup start time.");
            if (method == "Cash") throw new InvalidOperationException("Deposits must use GCash, Maya or Bank Transfer.");
            if (proof == null || proof.Length == 0) throw new InvalidOperationException("Upload a payment screenshot to submit your booking.");
            MixedSelectionInput?[]? input;
            try
            {
                input = JsonSerializer.Deserialize<MixedSelectionInput?[]>(mixedSelection ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException) { throw new InvalidOperationException("Choose a valid service and add-ons for every person."); }
            if (input == null || input.Length is < 2 or > BookingPricing.MaximumPeople || input.Any(person => person == null))
                throw new InvalidOperationException($"Choose between 2 and {BookingPricing.MaximumPeople} people.");
            var people = input.Select(person => new MixedPersonSelection(person!.ServiceId, person.AddOnIds ?? Array.Empty<Guid>())).ToArray();
            string? imageFingerprint = null;
            path = await SaveProof(proof, hash => imageFingerprint = hash);
            var booking = workflow.CreateMixed(UserId, people, mixedStart, address, area, deposit, method, reference, path, agreed, travelAcknowledged, expectedTotal.Value, expectedMinimum.Value, submissionId, policyVersion, travelReminderVersion, imageFingerprint);
            lock (store.Gate)
            {
                if (!store.Payments.Any(p => p.BookingId == booking.Id && p.ProofPath == path)) CleanupFailedStaging(path);
            }
            CompleteStaging(path);
            path = null;
            TempData["Success"] = "Your mixed-service booking is submitted. The artist will verify your payment and confirm the appointment.";
            if (Request.Headers.Accept.ToString().Contains("application/json")) return Json(new { confirmationUrl = Url.Action("Confirmation", new { id = booking.Id }) });
            return RedirectToAction("Confirmation", new { id = booking.Id });
        }
        catch (InvalidOperationException e) { if (Request.Headers.Accept.ToString().Contains("application/json")) return BadRequest(new { error = e.Message }); TempData["Error"] = e.Message; return RedirectToAction("Create"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { if (Request.Headers.Accept.ToString().Contains("application/json")) return StatusCode(503, new { error = "The payment screenshot could not be saved. Please try again." }); TempData["Error"] = "The payment screenshot could not be saved. Please try again."; return RedirectToAction("Create"); }
        finally { CleanupFailedStaging(path); }
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Confirmation(Guid id)
    {
        lock (store.Gate)
        {
            var b = store.Bookings.FirstOrDefault(b => b.Id == id && b.CustomerId == UserId);
            if (b == null) return NotFound();
            if (store.IsPersistent && b.MixedPeople.Count > 0) return NotFound();
            ViewBag.Paid = workflow.Paid(b);
            ViewBag.Payments = store.Payments.Where(p => p.BookingId == id).ToList();
            return View(b);
        }
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Details(Guid id)
    {
        lock (store.Gate)
        {
            var b = store.Bookings.FirstOrDefault(b => b.Id == id && b.CustomerId == UserId);
            if (b == null) return NotFound();
            if (store.IsPersistent && b.MixedPeople.Count > 0) return NotFound();
            ViewBag.Paid = workflow.Paid(b);
            ViewBag.Payments = store.Payments.Where(p => p.BookingId == id).ToList();
            return View(b);
        }
    }
    [HttpPost]
    public IActionResult Cancel(Guid id, bool confirmed, string? reason = null)
    {
        lock (store.Gate)
        {
            var b = store.Bookings.FirstOrDefault(b => b.Id == id && b.CustomerId == UserId);
            if (b == null) return NotFound();
            try
            {
                workflow.Cancel(b, UserId, confirmed, reason);
                TempData["Success"] = "Appointment cancelled. Any refund will be sent manually by the artist.";
            }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            return RedirectToAction("Details", new { id });
        }
    }
    [HttpPost]
    public IActionResult Reschedule(Guid id, DateTime start)
    {
        lock (store.Gate)
        {
            var b = store.Bookings.FirstOrDefault(b => b.Id == id && b.CustomerId == UserId);
            if (b == null) return NotFound();
            try
            {
                if (!ModelState.IsValid) throw new InvalidOperationException("Choose a valid appointment time.");
                var quote = workflow.OfferReschedule(b, UserId, start);
                return View("RescheduleReview", quote);
            }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            return RedirectToAction("Details", new { id });
        }
    }
    [HttpPost, RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> CorrectProof(Guid id, string reference, IFormFile? proof, int? paymentRevision = null, long? expectedPaymentRevision = null, string? expectedPaymentVersion = null)
    {
        string? path = null;
        try
        {
            if (store.IsPersistent)
            {
                if (ModelState.TryGetValue(nameof(expectedPaymentRevision), out var persistentRevisionState) && persistentRevisionState.Errors.Count > 0)
                    throw new InvalidOperationException("The payment proof changed. Reload and submit the latest proof correction.");
                BookingService.ValidatePersistentCorrectionToken(expectedPaymentRevision, expectedPaymentVersion);
                throw new InvalidOperationException("Persistent payment correction is unavailable until transaction-scoped authorization is verified.");
            }
            path = await SaveProof(proof);
            lock (store.Gate)
            {
                var b = store.Bookings.FirstOrDefault(b => b.Id == id && b.CustomerId == UserId);
                if (b == null) { if (path != null) System.IO.File.Delete(path); return NotFound(); }
                if (!store.IsPersistent && b.Status == "Pending Payment")
                {
                    var payment = store.Payments.FirstOrDefault(p => p.BookingId == b.Id);
                    if (payment != null && !payment.Verified && (!paymentRevision.HasValue || paymentRevision.Value != payment.Revision ||
                        (ModelState.TryGetValue(nameof(paymentRevision), out var revisionState) && revisionState.Errors.Count > 0)))
                        throw new InvalidOperationException("The payment proof changed. Reload and submit the latest proof correction.");
                }
                workflow.CorrectProof(b, UserId, reference, path, expectedPaymentRevision, expectedPaymentVersion);
            }
            CompleteStaging(path);
            path = null;
            TempData["Success"] = "Payment proof updated.";
        }
        catch (InvalidOperationException e) { CleanupFailedStaging(path); TempData["Error"] = e.Message; }
        return RedirectToAction("Details", new { id });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult ConfirmReschedule(Guid id, Guid quoteId, bool agreed)
    {
        lock (store.Gate)
        {
            var b = store.Bookings.FirstOrDefault(b => b.Id == id && b.CustomerId == UserId);
            if (b == null) return NotFound();
            try { workflow.ConsentReschedule(b, UserId, quoteId, agreed); TempData["Success"] = "Your request and consent were recorded. The original appointment remains reserved until SuperAdmin approval."; }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            return RedirectToAction("Details", new { id });
        }
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Proof(Guid id)
    {
        lock (store.Gate)
        {
            var payment = store.Payments.FirstOrDefault(x => x.Id == id);
            if (payment == null) return NotFound();
            var booking = store.Bookings.FirstOrDefault(x => x.Id == payment.BookingId && x.CustomerId == UserId);
            if (booking == null) return NotFound();
            if (store.IsPersistent && booking.MixedPeople.Count > 0) return NotFound();
            var asset = payment.ProofKey != null && store is IPersistentStudioStore persistent
                ? persistent.ReadAsset(payment.ProofKey)
                : payment.ProofPath != null && System.IO.File.Exists(payment.ProofPath)
                    ? (System.IO.File.ReadAllBytes(payment.ProofPath), payment.ProofPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg")
                    : null;
            if (asset == null) return NotFound();
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Disposition"] = "inline";
            return File(asset.Value.Item1, asset.Value.Item2);
        }
    }
    private async Task<string?> SaveProof(IFormFile? proof, Action<string>? capturePixelFingerprint = null)
    {
        if (proof == null || proof.Length == 0) return null;
        if (proof.Length > PaymentProofImageValidator.MaximumBytes) throw new InvalidOperationException("Upload a JPG or PNG up to 5 MB.");
        using var memory = new MemoryStream();
        await proof.CopyToAsync(memory);
        var bytes = memory.ToArray();
        if (capturePixelFingerprint != null) capturePixelFingerprint(PaymentProofImageValidator.PixelFingerprint(bytes));
        var normalized = PaymentProofImageValidator.Normalize(bytes);
        var folder = Path.Combine(environment.ContentRootPath, "App_Data", "Proofs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid() + normalized.Extension);
        try { await System.IO.File.WriteAllBytesAsync(path, normalized.Bytes); }
        catch { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); throw; }
        return path;
    }
    private void CompleteStaging(string? path)
    {
        if (path != null && files is SupabaseAppFileStore && System.IO.File.Exists(path)) System.IO.File.Delete(path);
    }
    private void CleanupFailedStaging(string? path)
    {
        if (path == null) return;
        if (files is SupabaseAppFileStore)
        {
            files.Delete("Proofs/" + Path.GetFileName(path));
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
    }
    private sealed record MixedSelectionInput(Guid ServiceId, Guid[]? AddOnIds);
}
