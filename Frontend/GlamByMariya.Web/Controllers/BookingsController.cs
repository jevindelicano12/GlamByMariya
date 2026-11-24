using System.Security.Claims;
using System.Text.Json;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Infrastructure.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace GlamByMariya.Web.Controllers;
[Authorize(Roles = "Customer")]
public class BookingsController(IStudioStore store, BookingService workflow, IWebHostEnvironment environment, CatalogImageService imageService, IAppFileStore files, XenditClient? xendit = null) : Controller
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
        if (!ModelState.IsValid || people == null) return BadRequest(new { error = "Choose a valid service and add-ons for each person." });
        try { return Json(workflow.QuoteMixed(people)); }
        catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(32768), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult MixedAvailability([FromBody] MixedAvailabilityRequest request)
    {
        if (!ModelState.IsValid || request?.People == null || !DateTime.TryParseExact(request.Month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var month))
            return BadRequest(new { error = "Choose a valid month and services for every person." });
        lock (store.Gate)
        {
            try { return Json(workflow.GetMixedAvailability(month, workflow.QuoteMixed(request.People).DurationMinutes, request.Latitude, request.Longitude, HttpContext.RequestAborted)); }
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
        if (!ModelState.IsValid || request?.People == null || !DateTime.TryParseExact(request.Start, "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start))
            return BadRequest(new { error = "Choose a valid date, time and services for every person." });
        lock (store.Gate)
        {
            try
            {
                var quote = workflow.QuoteMixed(request.People, start);
                var calendar = workflow.GetMixedAvailability(start, quote.DurationMinutes, request.Latitude, request.Longitude, HttpContext.RequestAborted);
                var slot = calendar.Days.SelectMany(day => day.Slots).FirstOrDefault(candidate => candidate.Start == request.Start);
                if (slot == null || !slot.Available) return Json(new { available = false, message = slot?.Reason ?? "This time is outside the available booking slots." });
                workflow.CheckSlot(start, quote.DurationMinutes);
                return Json(new { available = true, quote, bufferMinutes = calendar.BufferMinutes, message = request.Latitude.HasValue ? "Available for request at this moment. Times include travel from the artist's other appointments that day." : "Availability is based on makeup duration, preparation time and existing appointments. Travel feasibility has not yet been verified." });
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
    public IActionResult Availability(string month, Guid serviceId, Guid[] addOnIds, Guid? bookingId, int people = 1, string package = "auto", decimal? latitude = null, decimal? longitude = null)
    {
        if (!DateTime.TryParseExact(month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)) return BadRequest(new { error = "Choose a valid month." });
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid availability request." });
        lock (store.Gate)
        {
            try
            {
                var selection = AvailabilitySelection(serviceId, addOnIds, bookingId, people, package);
                if (selection == null) return NotFound(new { error = "This appointment cannot be rescheduled." });
                return Json(workflow.GetAvailability(date, selection.Value.Duration, selection.Value.Exclude, latitude, longitude, HttpContext.RequestAborted));
            }
            catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        }
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult ValidateSlot(string start, Guid serviceId, Guid[] addOnIds, Guid? bookingId, int people = 1, string package = "auto", decimal? latitude = null, decimal? longitude = null)
    {
        if (!DateTime.TryParseExact(start, "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) || !ModelState.IsValid) return BadRequest(new { error = "Choose a valid date and time." });
        lock (store.Gate)
        {
            try
            {
                var selection = AvailabilitySelection(serviceId, addOnIds, bookingId, people, package);
                if (selection == null) return NotFound(new { error = "This appointment cannot be rescheduled." });
                workflow.CheckSlot(date, selection.Value.Duration, selection.Value.Exclude);
                workflow.CheckTravel(date, selection.Value.Duration, selection.Value.Exclude, latitude, longitude);
                // An early time (new booking) is a request: the artist confirms it before anything is paid.
                var early = bookingId == null && BookingWindow.KindOf(date, store.Settings) == SlotKind.Early;
                var next = early ? "The artist confirms this early time first; you pay the down payment after approval."
                    : UsesXendit ? "Your booking is confirmed once the down payment goes through." : "Confirmation requires payment review and approval.";
                return Json(new { available = true, message = (UsesXendit ? "This time is available." : "Available for request at this moment.")
                    + " Times include travel from the artist's other appointments that day. " + next });
            }
            catch (InvalidOperationException e) { return Json(new { available = false, message = e.Message }); }
        }
    }
    private (int Duration, Guid? Exclude)? AvailabilitySelection(Guid serviceId, Guid[] addOnIds, Guid? bookingId, int people, string package)
    {
        if (!bookingId.HasValue) return (workflow.SelectedDuration(serviceId, addOnIds, people, package), null);
        var b = store.FindBooking(bookingId.Value) is { } upcoming && upcoming.CustomerId == UserId && upcoming.Status == "Confirmed" && upcoming.Start > store.Now ? upcoming : null;
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
        ViewBag.XenditEnabled = UsesXendit;
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
    public async Task<IActionResult> Create(Guid serviceId, Guid[] addOnIds, DateTime start, string address, string area, decimal deposit, string method, string? reference, IFormFile? proof, bool agreed, int people = 1, string package = "auto", bool travelAcknowledged = false, decimal? expectedTotal = null, decimal? expectedMinimum = null, decimal? latitude = null, decimal? longitude = null, bool afterHoursAccepted = false)
    {
        string? path = null;
        try
        {
            if (!ModelState.IsValid) throw new InvalidOperationException("Check the number of people, date and payment amount.");
            if (!expectedTotal.HasValue || !expectedMinimum.HasValue) throw new InvalidOperationException("Review the current price before submitting.");
            // Checked before any payment screenshot is saved; BookingService checks the pin again.
            if (!BookingLocation.IsValid(latitude, longitude)) throw new InvalidOperationException(BookingLocation.Message);
            RequireAfterHoursTerms(start, workflow.Quote(serviceId, addOnIds, people, package, start).DurationMinutes, afterHoursAccepted);
            if (UsesXendit) return await CreateWithXendit(serviceId, addOnIds, start, address, area, deposit, agreed, people, package, travelAcknowledged, expectedTotal, expectedMinimum, latitude, longitude);
            if (method == "Cash") throw new InvalidOperationException("Deposits must use GCash, Maya or Bank Transfer.");
            if (proof == null || proof.Length == 0) throw new InvalidOperationException("Upload a payment screenshot to submit your booking.");
            // Nullable only because the Xendit form has no reference field; the manual flow still requires one.
            if (string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException("Enter your payment reference number.");
            path = await SaveProof(proof);
            workflow.Create(UserId, serviceId, addOnIds, start, address, area, deposit, method, reference, path, agreed, people, package, travelAcknowledged, expectedTotal, expectedMinimum, latitude: latitude, longitude: longitude);
            CompleteStaging(path);
            path = null;
            TempData["Success"] = "Your booking is submitted. The artist will verify your deposit and confirm your appointment.";
            return RedirectToAction("Index");
        }
        catch (InvalidOperationException e) { CleanupFailedStaging(path); TempData["Error"] = e.Message; return RedirectToAction("Create", new { serviceId }); }
    }
    // Tells the page whether this booking is paid on Xendit and, while it waits for the
    // down payment, the link back to its Xendit payment page.
    private void ShowXenditPayment(Booking booking)
    {
        ViewBag.PaidOnXendit = ((List<Payment>)ViewBag.Payments).Any(p => p.Method == "Xendit");
        // The amount the customer chose to pay online (from the down payment up to the total).
        ViewBag.PayAmount = ((List<Payment>)ViewBag.Payments).Select(p => (decimal?)p.Amount).FirstOrDefault() ?? booking.RequiredDownPayment;
        ViewBag.CheckoutUrl = UsesXendit && booking.Status == "Pending Payment"
            ? ((IPersistentStudioStore)store).PendingCheckoutUrl(booking.Id) : null;
        // An approved early request has no Xendit page yet: the customer opens it with "Pay down payment".
        ViewBag.PayApprovedRequest = UsesXendit && booking.IsEarlyRequest && booking.Status == "Pending Payment" && ViewBag.CheckoutUrl == null;
    }

    // Pay after approval: opens (or reopens) the Xendit page for an approved early request. If Xendit
    // cannot open it, the approval stays and the customer can try again within the 15 minutes.
    public const string PayRequestTooLateMessage = "The 15-minute payment time after approval has ended (or is about to). Nothing was charged; ask the artist to approve the time again.";
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PayRequest(Guid id)
    {
        Booking? b;
        lock (store.Gate) b = store.FindBooking(id) is { } owned && owned.CustomerId == UserId ? owned : null;
        if (b == null) return NotFound();
        if (!UsesXendit || !b.IsEarlyRequest || b.Status != "Pending Payment")
        {
            TempData["Error"] = b.Status == BookingService.EarlyRequestStatus ? "The artist has not approved this early time yet." : "This booking is not waiting for a down payment.";
            return RedirectToAction("Details", new { id });
        }
        var database = (IPersistentStudioStore)store;
        if (database.PendingCheckoutUrl(id) is { } open) return Redirect(open);
        // The hold started at approval, so the Xendit page must close before it ends.
        var seconds = b.PaymentDueAt is { } due ? GlamByMariya.Infrastructure.Integrations.XenditClient.SecondsBefore(due, store.Now) : null;
        if (seconds == null)
        {
            TempData["Error"] = PayRequestTooLateMessage;
            return RedirectToAction("Details", new { id });
        }
        try
        {
            var email = User.FindFirstValue(ClaimTypes.Email) ?? "";
            var done = Url.Action("Confirmation", "Bookings", new { id }, Request.Scheme)!;
            // The amount the customer chose when sending the request (from 50% up to the total).
            var amount = store.Payments.Where(p => p.BookingId == id).Select(p => p.Amount).DefaultIfEmpty(b.RequiredDownPayment).First();
            var invoice = await xendit!.CreateInvoiceAsync(b.Reference, amount, email,
                $"Glam by Mariya down payment for {b.Reference} ({b.ServiceDescription})", done, done, durationSeconds: seconds);
            database.AttachInvoice(id, invoice.Id, invoice.InvoiceUrl);
            return Redirect(invoice.InvoiceUrl);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            TempData["Error"] = "We could not open the Xendit payment page. Nothing was charged; please try again before the payment time ends.";
            return RedirectToAction("Details", new { id });
        }
    }
    // Database mode with Xendit configured: the down payment is paid on Xendit's checkout.
    private bool UsesXendit => xendit?.Enabled == true && store is IPersistentStudioStore;

    // Saves the booking as Pending Payment, opens a Xendit invoice for the exact down payment
    // and sends the customer to it. Only the Xendit webhook confirms the booking. If the invoice
    // cannot be created, the unpaid booking is cancelled (free) so the slot is released at once.
    // A time that ends after closing is allowed, but the customer agrees that any after-hours fee is settled
    // with the artist outside the website (Terms: After-hours appointments).
    public const string AfterHoursTermsMessage = "This time ends after closing. Tick \"I understand and agree to the after-hours terms\" to continue.";
    private void RequireAfterHoursTerms(DateTime start, int minutes, bool accepted)
    {
        if (!accepted && BookingWindow.IsAfterHours(start, minutes, store.Settings)) throw new InvalidOperationException(AfterHoursTermsMessage);
    }

    private async Task<IActionResult> CreateWithXendit(Guid serviceId, Guid[] addOnIds, DateTime start, string address, string area, decimal deposit, bool agreed,
        int people, string package, bool travelAcknowledged, decimal? expectedTotal, decimal? expectedMinimum, decimal? latitude, decimal? longitude)
    {
        var reference = "XENDIT-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var booking = workflow.Create(UserId, serviceId, addOnIds, start, address, area, deposit, "Xendit", reference, null, agreed, people, package, travelAcknowledged, expectedTotal, expectedMinimum, latitude: latitude, longitude: longitude);
        return Redirect(await NextStepUrl(booking, deposit));
    }

    // Phase 6: a mixed package on the live site, paid on Xendit like a single booking.
    private async Task<IActionResult> CreateMixedWithXendit(DateTime start, string address, string area, decimal deposit, bool agreed, bool travelAcknowledged,
        decimal? expectedTotal, decimal? expectedMinimum, string? mixedSelection, decimal? latitude, decimal? longitude, bool afterHoursAccepted)
    {
        var wantsJson = Request.Headers.Accept.ToString().Contains("application/json");
        try
        {
            if (!expectedTotal.HasValue || !expectedMinimum.HasValue) throw new InvalidOperationException("Review the current package price before submitting.");
            MixedSelectionInput?[]? input;
            try { input = JsonSerializer.Deserialize<MixedSelectionInput?[]>(mixedSelection ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { throw new InvalidOperationException("Choose a valid service and add-ons for every person."); }
            if (input == null || input.Length is < 2 or > BookingPricing.MaximumPeople || input.Any(person => person == null))
                throw new InvalidOperationException($"Choose between 2 and {BookingPricing.MaximumPeople} people.");
            var people = input.Select(person => new MixedPersonSelection(person!.ServiceId, person.AddOnIds ?? Array.Empty<Guid>())).ToArray();
            RequireAfterHoursTerms(start, workflow.QuoteMixed(people, start).DurationMinutes, afterHoursAccepted);
            var reference = "XENDIT-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
            var booking = workflow.CreateMixed(UserId, people, start, address, area, deposit, "Xendit", reference, null, agreed, travelAcknowledged,
                expectedTotal.Value, expectedMinimum.Value, latitude: latitude, longitude: longitude);
            var next = await NextStepUrl(booking, deposit);
            return wantsJson ? Json(new { confirmationUrl = next }) : Redirect(next);
        }
        catch (InvalidOperationException e)
        {
            if (wantsJson) return BadRequest(new { error = e.Message });
            TempData["Error"] = e.Message; return RedirectToAction("Create");
        }
    }

    // After a live booking is saved: an early request waits for the artist (no invoice yet); anything
    // else opens the Xendit checkout. If the invoice cannot be created, the unpaid booking is cancelled
    // (free) so the slot is released at once.
    private async Task<string> NextStepUrl(Booking booking, decimal amount)
    {
        var database = (IPersistentStudioStore)store;
        if (booking.IsEarlyRequest)
        {
            TempData["Success"] = BookingService.EarlyRequestSentMessage;
            return Url.Action("Confirmation", new { id = booking.Id })!;
        }
        try
        {
            var email = User.FindFirstValue(ClaimTypes.Email) ?? "";
            var done = Url.Action("Confirmation", "Bookings", new { id = booking.Id }, Request.Scheme)!;
            var invoice = await xendit!.CreateInvoiceAsync(booking.Reference, amount, email,
                $"Glam by Mariya down payment for {booking.Reference} ({booking.ServiceDescription})", done, done);
            database.AttachInvoice(booking.Id, invoice.Id, invoice.InvoiceUrl);
            return invoice.InvoiceUrl;
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            try { database.CancelBooking(booking.Id, UserId, true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("We could not open the Xendit payment page, so nothing was charged and the time was released. Please try again.");
        }
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> CreateMixed(DateTime mixedStart, string address, string area, decimal deposit, string method, string reference, IFormFile? proof, bool agreed, bool travelAcknowledged, decimal? expectedTotal, decimal? expectedMinimum, string? mixedSelection, Guid submissionId, string? policyVersion, string? travelReminderVersion, decimal? latitude = null, decimal? longitude = null, bool afterHoursAccepted = false)
    {
        // Live site (Phase 6): a package is paid on Xendit like a single booking (no screenshot).
        if (UsesXendit) return await CreateMixedWithXendit(mixedStart, address, area, deposit, agreed, travelAcknowledged, expectedTotal, expectedMinimum, mixedSelection, latitude, longitude, afterHoursAccepted);
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
            RequireAfterHoursTerms(mixedStart, workflow.QuoteMixed(people, mixedStart).DurationMinutes, afterHoursAccepted);
            string? imageFingerprint = null;
            path = await SaveProof(proof, hash => imageFingerprint = hash);
            var booking = workflow.CreateMixed(UserId, people, mixedStart, address, area, deposit, method, reference, path, agreed, travelAcknowledged, expectedTotal.Value, expectedMinimum.Value, submissionId, policyVersion, travelReminderVersion, imageFingerprint, latitude, longitude);
            lock (store.Gate)
            {
                if (!store.Payments.Any(p => p.BookingId == booking.Id && p.ProofPath == path)) CleanupFailedStaging(path);
            }
            CompleteStaging(path);
            path = null;
            TempData["Success"] = booking.IsEarlyRequest ? BookingService.EarlyRequestSentMessage : "Your mixed-service booking is submitted. The artist will verify your payment and confirm the appointment.";
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
            var b = store.FindBooking(id) is { } owned && owned.CustomerId == UserId ? owned : null;
            if (b == null) return NotFound();

            ViewBag.Paid = workflow.Paid(b);
            ViewBag.Payments = store.Payments.Where(p => p.BookingId == id).ToList();
            ShowXenditPayment(b);
            return View(b);
        }
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Details(Guid id)
    {
        lock (store.Gate)
        {
            var b = store.FindBooking(id) is { } owned && owned.CustomerId == UserId ? owned : null;
            if (b == null) return NotFound();

            ViewBag.Paid = workflow.Paid(b);
            ViewBag.Payments = store.Payments.Where(p => p.BookingId == id).ToList();
            ShowXenditPayment(b);
            return View(b);
        }
    }
    [HttpPost]
    public IActionResult Cancel(Guid id, bool confirmed, string? reason = null)
    {
        lock (store.Gate)
        {
            var b = store.FindBooking(id) is { } owned && owned.CustomerId == UserId ? owned : null;
            if (b == null) return NotFound();
            try
            {
                workflow.Cancel(b, UserId, confirmed, reason);
                var updated = store.FindBooking(id) ?? b;
                var refundLine = updated.RefundDue > 0
                    ? (updated.CancellationFee > 0
                        ? $"A cancellation fee of ₱{updated.CancellationFee:N2} applies. Your remaining refund of ₱{updated.RefundDue:N2} has not been refunded yet — it will be processed manually by the artist, and you will receive another email once it is sent."
                        : $"No cancellation fee applies. Your refund of ₱{updated.RefundDue:N2} has not been refunded yet — it will be processed manually by the artist, and you will receive another email once it is sent.")
                    : (updated.CancellationFee > 0
                        ? $"A cancellation fee of ₱{updated.CancellationFee:N2} applies, so no refund will be issued for this booking."
                        : "No payment was collected for this booking, so no refund is needed.");
                EmailCustomer(updated.CustomerId, $"Glam by Mariya {updated.Reference}: your booking was cancelled",
                    $"Your booking for {updated.ServiceDescription} on {updated.Start:MMMM d, yyyy h:mm tt} has been cancelled.{Environment.NewLine}{Environment.NewLine}{refundLine}{Environment.NewLine}{Environment.NewLine}If you have any questions, please reply to this email or contact Glam by Mariya.");
                TempData["Success"] = "Appointment cancelled. Any refund will be sent manually by the artist.";
            }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            return RedirectToAction("Details", new { id });
        }
    }
    private void EmailCustomer(Guid customerId, string subject, string text)
    {
        var to = store.Users.FirstOrDefault(u => u.Id == customerId)?.Email;
        var brevo = HttpContext?.RequestServices.GetService(typeof(GlamByMariya.Infrastructure.Integrations.BrevoEmailSender)) as GlamByMariya.Infrastructure.Integrations.BrevoEmailSender;
        var log = HttpContext?.RequestServices.GetService(typeof(ILogger<BookingsController>)) as ILogger<BookingsController>;
        if (string.IsNullOrWhiteSpace(to) || brevo == null) return;
        _ = Task.Run(async () =>
        {
            try { await brevo.SendAsync(to, subject, text); }
            catch (Exception e) { log?.LogWarning("Booking cancellation email was not sent: {Reason}", e.Message); }
        });
    }
    [HttpPost]
    public IActionResult Reschedule(Guid id, DateTime start)
    {
        lock (store.Gate)
        {
            var b = store.FindBooking(id) is { } owned && owned.CustomerId == UserId ? owned : null;
            if (b == null) return NotFound();
            try
            {
                if (!ModelState.IsValid) throw new InvalidOperationException("Choose a valid appointment time.");
                var quote = workflow.OfferReschedule(b, UserId, start);
                ViewBag.MixedBooking = b.MixedPeople.Count > 0; return View("RescheduleReview", quote);
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
                var b = store.FindBooking(id) is { } owned && owned.CustomerId == UserId ? owned : null;
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
    public IActionResult ConfirmReschedule(Guid id, Guid quoteId, bool agreed, DateTime? proposedStart = null, decimal? expectedTotal = null)
    {
        lock (store.Gate)
        {
            var b = store.FindBooking(id) is { } owned && owned.CustomerId == UserId ? owned : null;
            if (b == null) return NotFound();
            try { workflow.ConsentReschedule(b, UserId, quoteId, agreed, proposedStart, expectedTotal); TempData["Success"] = "Your request and consent were recorded. The original appointment remains reserved until SuperAdmin approval."; }
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
            var booking = store.FindBooking(payment.BookingId) is { } paid && paid.CustomerId == UserId ? paid : null;
            if (booking == null) return NotFound();

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
