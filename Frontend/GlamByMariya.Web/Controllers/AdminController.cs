using System.Security.Claims;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace GlamByMariya.Web.Controllers;
[Authorize(Roles = "Admin,SuperAdmin")]
public class AdminController(IStudioStore store, BookingService workflow, CatalogImageService imageService, IInvitationService invites, EmailVerificationService mail) : Controller
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        workflow.ExpireOverduePayments();
        base.OnActionExecuting(context);
    }
    private HashSet<Guid> CorrectionBookings(IEnumerable<Booking> bookings, IEnumerable<Payment> payments)
    {
        var corrected = payments.Where(p => p.ReviewStatus == "Correction Requested").Select(p => p.BookingId).ToHashSet();
        return bookings.Where(b => b.Status == "Pending Payment" && corrected.Contains(b.Id) &&
            workflow.CanManageBooking(b, UserId))
            .Select(b => b.Id).ToHashSet();
    }

    // SuperAdmin-only: administrator roster, pending invitations, invite form.
    // Plain Admins keep full booking/catalog/settings access but never see this.
    [Authorize(Roles = "SuperAdmin")]
    public IActionResult Users()
    {
        lock (store.Gate)
        {
            ViewBag.Admins = store.Users.Where(u => u.Role is "Admin" or "SuperAdmin").OrderBy(u => u.Role).ThenBy(u => u.Email).ToList();
            ViewBag.Invites = invites.Pending();
            return View();
        }
    }

    [Authorize(Roles = "SuperAdmin")]
    [HttpPost, EnableRateLimiting(RateLimitPolicies.InviteAdmin)]
    public async Task<IActionResult> Invite(string email)
    {
        email = (email ?? "").Trim();
        try
        {
            lock (store.Gate)
            {
                if (store is PostgresStudioStore database ? database.FindUser(email) != null
                    : store.Users.Any(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("This email is already registered.");
                if (invites.Pending().Any(i => i.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("An administrator invitation is already pending for this email.");
            }
            var (invite, token) = invites.InviteWithToken(email, UserId);
            var link = Url.Action("AcceptInvite", "Account", new { email = invite.Email, token }, Request.Scheme)
                ?? $"/Account/AcceptInvite?email={Uri.EscapeDataString(invite.Email)}&token={Uri.EscapeDataString(token)}";
            try
            {
                await mail.SendEmailAsync(invite.Email, "You are invited to administer Glam by Mariya",
                    $"A Super Admin invited you to administer Glam by Mariya.\n\nOpen this link within 72 hours to create your administrator account and set your password (single use):\n{link}\n\nIf you did not expect this, ignore this email.");
            }
            catch
            {
                // A failed API send must not leave an apparently usable
                // invitation that the recipient never received.
                invites.Revoke(invite.Id, UserId);
                throw;
            }
            store.RecordAudit(UserId, $"Invited administrator {invite.Email}.");
            TempData["Success"] = $"Invitation sent to {invite.Email}. It expires in 72 hours.";
        }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Users));
    }

    [Authorize(Roles = "SuperAdmin")]
    [HttpPost]
    public IActionResult RevokeInvite(Guid id)
    {
        try
        {
            invites.Revoke(id, UserId);
            store.RecordAudit(UserId, "Revoked an administrator invitation.");
            TempData["Success"] = "Invitation revoked.";
        }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Users));
    }

    // Replaces the removed admin roster page: the owner disables or enables an Admin.
    // A disabled Admin is signed out on the next request by the per-request cookie check.
    [Authorize(Roles = "SuperAdmin")]
    [HttpPost]
    public IActionResult SetAdminActive(Guid id, bool active)
    {
        try
        {
            lock (store.Gate) store.SetAdminActive(id, active, UserId);
            store.RecordAudit(UserId, active ? "Enabled an administrator account." : "Disabled an administrator account.");
            TempData["Success"] = active ? "Administrator enabled." : "Administrator disabled. They are signed out on their next click.";
        }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Users));
    }

    public IActionResult Index(string? status)
    {
        lock (store.Gate)
        {
            var all = store.Bookings.ToList(); var payments = store.Payments.ToList();
            ViewBag.All = all; ViewBag.Payments = payments; ViewBag.Status = status;
            ViewBag.CorrectionBookingIds = CorrectionBookings(all, payments);
            return View(all.Where(b => string.IsNullOrEmpty(status) || b.Status == status).OrderBy(b => b.Start).ToList());
        }
    }
    // Does the proposed reschedule time fit the artist's travel? null when there is nothing to check (no pending request, or a mixed booking).
    private bool? RescheduleTravelFits(Booking b)
    {
        if (b.MixedPeople.Count > 0) return null;
        var proposed = b.PendingReschedule?.Quote.ProposedStart ?? (store.IsPersistent && b.Status == "Confirmed" ? b.RequestedStart : null);
        if (proposed is not { } start) return null;
        return BookingService.TravelFits(store, start, (int)(b.End - b.Start).TotalMinutes, b.BufferMinutesSnapshot ?? store.Settings.BufferMinutes, b.Latitude, b.Longitude, b.Id);
    }
    public IActionResult Details(Guid id)
    {
        lock (store.Gate)
        {
            var b = store.FindBooking(id); if (b == null) return NotFound();
            if (!workflow.CanManageBooking(b, UserId)) return Forbid();
            ViewBag.TravelFits = RescheduleTravelFits(b); ViewBag.Paid = workflow.Paid(b); ViewBag.Payments = store is IPersistentStudioStore persistent ? persistent.ReviewPayments(id) : store.Payments.Where(p => p.BookingId == id).ToList(); return View(b);
        }
    }
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Action(Guid id, string operation, string reason = "", decimal amount = 0, string reference = "", Guid? paymentId = null, int? paymentRevision = null, bool reviewed = false, DateTime? requestedStart = null, Guid? rescheduleQuoteId = null, bool confirmed = false, long? reviewRevision = null, string? reviewVersion = null, bool travelOverride = false)
    {
        lock (store.Gate)
        {
            var b = store.FindBooking(id); if (b == null) return NotFound();
            if (!workflow.CanManageBooking(b, UserId)) return Forbid();
            try
            {
                if (operation == "verify" && !store.IsPersistent && b.MixedPeople.Count == 0 &&
                    ModelState.TryGetValue(nameof(paymentRevision), out var revisionState) && revisionState.Errors.Count > 0)
                    throw new InvalidOperationException("The payment claim changed. Reload and inspect the latest proof before verifying.");
                if (store.IsPersistent && (operation is "verify" or "approve") &&
                    ModelState.TryGetValue(nameof(reviewRevision), out var reviewState) && reviewState.Errors.Count > 0)
                    throw new InvalidOperationException("Reload and inspect the latest payment proof before verifying.");
                if (!ModelState.IsValid) throw new InvalidOperationException("Check the action details and try again.");
                if (store.IsPersistent && (operation is "verify" or "approve") && (!paymentId.HasValue || paymentId == Guid.Empty || !reviewRevision.HasValue || reviewRevision < 0 || !uint.TryParse(reviewVersion, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _) || !reviewed))
                    throw new InvalidOperationException("Reload and inspect the latest payment proof and confirm your review before verifying.");
                if (operation == "verify-cancelled-receipt")
                {
                    if (!User.IsInRole("SuperAdmin") || store.IsPersistent) return Forbid();
                    if (!paymentId.HasValue || !paymentRevision.HasValue) throw new InvalidOperationException("Reload and inspect the payment claim before verifying.");
                    workflow.VerifyCancelledReceipt(b, UserId, paymentId.Value, paymentRevision.Value, reviewed);
                    TempData["Success"] = "Payment receipt verified. The appointment remains cancelled; refund arrangements are separate.";
                    return RedirectToAction("Details", new { id });
                }
                if ((operation is "cancel" or "complete") && !User.IsInRole("SuperAdmin")) return Forbid();
                if (operation == "reschedule" && !User.IsInRole("SuperAdmin")) return Forbid();
                // Demo keeps the consented quote; database mode only knows the requested start, checked below.
                if (operation == "reschedule" && !store.IsPersistent && (rescheduleQuoteId == null || b.PendingReschedule?.Quote.Id != rescheduleQuoteId)) throw new InvalidOperationException("The reschedule request changed. Reload before deciding.");
                if (!store.IsPersistent && b.MixedPeople.Count == 0 && operation == "verify")
                {
                    var payments = store.Payments.Where(p => p.BookingId == b.Id).Take(2).ToArray();
                    if (payments.Length != 1 || paymentId != payments[0].Id || !paymentRevision.HasValue || paymentRevision.Value != payments[0].Revision)
                        throw new InvalidOperationException("The payment claim changed. Reload and inspect the latest proof before verifying.");
                }
                if (!store.IsPersistent && b.MixedPeople.Count > 0 && operation == "verify")
                {
                    var payment = store.Payments.SingleOrDefault(p => p.BookingId == b.Id && p.Id == paymentId);
                    if (payment == null || paymentRevision != payment.Revision) throw new InvalidOperationException("The payment claim changed. Reload and inspect the latest proof before verifying.");
                    if (!reviewed) throw new InvalidOperationException("Confirm that you checked the actual transfer and this payment proof before verifying.");
                    if ((payment.ProofPath == null || !System.IO.File.Exists(payment.ProofPath)) && payment.ProofKey == null) throw new InvalidOperationException("The submitted proof is unavailable. Request a replacement before verifying.");
                }
                if ((b.MixedPeople.Count > 0 || store.IsPersistent) && (operation is "reschedule" or "decline") && (!requestedStart.HasValue || b.RequestedStart != requestedStart))
                    throw new InvalidOperationException("The requested time changed. Reload this booking before deciding.");
                workflow.AdminAction(b, operation, UserId, reason, amount, reference, confirmed, paymentId, reviewRevision, reviewVersion, travelOverride);
                TempData["Success"] = operation switch
                {
                    "cancel" => "Appointment cancelled. Payment and refund arrangements require personal review.",
                    "complete" => "Appointment marked completed. Payment status was not changed.",
                    "approve-request" => "Early booking request approved. The customer has 15 minutes to pay the down payment.",
                    "decline-request" => "Early booking request declined. The customer was told why.",
                    _ => "Booking updated successfully."
                };
                if (operation is "approve-request" or "decline-request")
                    EmailCustomer(b.CustomerId, $"Glam by Mariya {b.Reference}: early booking request",
                        operation == "approve-request"
                            ? $"{BookingService.EarlyRequestApprovedMessage}{Environment.NewLine}{Environment.NewLine}Open your booking to pay: {Url.Action("Details", "Bookings", new { id }, Request.Scheme)}"
                            : string.Format(BookingService.EarlyRequestDeclinedMessage, reason.Trim()));
            }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            return RedirectToAction("Details", new { id });
        }
    }
    // Best-effort email after an early request is decided; the in-app notification is already saved,
    // so a missing Brevo key or a slow send never undoes or delays the decision.
    private void EmailCustomer(Guid customerId, string subject, string text)
    {
        var to = store.Users.FirstOrDefault(u => u.Id == customerId)?.Email;
        var brevo = HttpContext?.RequestServices.GetService(typeof(GlamByMariya.Infrastructure.Integrations.BrevoEmailSender)) as GlamByMariya.Infrastructure.Integrations.BrevoEmailSender;
        var log = HttpContext?.RequestServices.GetService(typeof(ILogger<AdminController>)) as ILogger<AdminController>;
        if (string.IsNullOrWhiteSpace(to) || brevo == null) return;
        _ = Task.Run(async () =>
        {
            try { await brevo.SendAsync(to, subject, text); }
            catch (Exception e) { log?.LogWarning("Early request email was not sent: {Reason}", e.Message); }
        });
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Proof(Guid id, Guid? bookingId = null, long? reviewRevision = null, string? reviewVersion = null)
    {
        lock (store.Gate)
        {
            var p = store.Payments.FirstOrDefault(x => x.Id == id);
            if (p == null) return NotFound();
            var booking = store.FindBooking(p.BookingId);
            if (booking == null || (bookingId.HasValue && bookingId != booking.Id)) return NotFound();
            if (!workflow.CanManageBooking(booking, UserId)) return Forbid();
            if (p.ProofKey != null && store is IPersistentStudioStore persistent)
            {
                var current = persistent.ReviewPayments(booking.Id).SingleOrDefault(x => x.Id == id);
                if (reviewRevision == null || reviewRevision < 0 || !uint.TryParse(reviewVersion, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _) || current?.PersistentReviewRevision != reviewRevision || current.PersistentReviewVersion != reviewVersion || current.ProofKey == null)
                    return Conflict("The payment claim changed. Reload and inspect the latest payment proof.");
                var asset = persistent.ReadAsset(current.ProofKey);
                if (asset == null) return NotFound();
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                Response.Headers["Content-Disposition"] = "inline";
                return File(asset.Value.Bytes, asset.Value.ContentType);
            }
            if (p.ProofPath == null || !System.IO.File.Exists(p.ProofPath)) return NotFound();
            if (!store.IsPersistent)
            {
                var root = Path.GetFullPath(Path.Combine(HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "App_Data", "Proofs")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(p.ProofPath).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return NotFound();
            }
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Disposition"] = "inline";
            return PhysicalFile(p.ProofPath, p.ProofPath.EndsWith(".png") ? "image/png" : "image/jpeg");
        }
    }

    [Authorize(Roles = "SuperAdmin")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Activity(string? search, string? category)
    {
        if (store.IsPersistent || store is not DemoStore demo) return NotFound();
        search = (search ?? "").Trim(); category = (category ?? "").Trim();
        if (search.Length > 100 || category.Length > 30) return BadRequest();
        var items = demo.AdminActivity().Where(x => (category.Length == 0 || x.TargetType == category) &&
            (search.Length == 0 || x.ActorName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             x.Action.Contains(search, StringComparison.OrdinalIgnoreCase) || x.ActorId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
             x.TargetId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))).OrderByDescending(x => x.OccurredAt).ToList();
        ViewBag.Search = search; ViewBag.Category = category;
        return View(items);
    }
    // Customers: every customer with when they last booked; one customer's page lists every booking with its time of booking.
    public IActionResult Customers(string? q, string? sort)
    {
        if (sort is not (null or "" or "recent" or "since")) return BadRequest();
        if (q?.Length > 100) q = q[..100];
        lock (store.Gate)
        {
            ViewBag.Search = q?.Trim() ?? ""; ViewBag.Sort = sort == "since" ? "since" : "recent";
            return View(CustomerHistory.List(store.Users, store.Bookings, store.BookingCreatedTimes(), store.CustomerCreatedTimes(), q, sort));
        }
    }
    public IActionResult Customer(Guid id)
    {
        lock (store.Gate)
        {
            var detail = CustomerHistory.Detail(id, store.Users, store.Bookings, store.BookingCreatedTimes(), store.CustomerCreatedTimes());
            return detail == null ? NotFound() : View(detail);
        }
    }
    public IActionResult Appointments(string? mode, string? status, int? year, int? month, int? day)
    {
        if (mode is not (null or "list" or "calendar") || year.HasValue != month.HasValue ||
            (year.HasValue && (year < 1901 || year > 9998 || month < 1 || month > 12))) return BadRequest();
        var now = store.Now;
        var first = new DateTime(year ?? now.Year, month ?? now.Month, 1);
        if (day.HasValue && (day < 1 || day > DateTime.DaysInMonth(first.Year, first.Month))) return BadRequest();
        lock (store.Gate)
        {
            var all = store.Bookings.OrderBy(b => b.Start).ToList();
            ViewBag.All = all; ViewBag.Status = status; ViewBag.Mode = mode ?? "list";
            if (mode != "calendar") ViewBag.CorrectionBookingIds = CorrectionBookings(all, store.Payments);
            ViewBag.Month = first; ViewBag.Day = day; ViewBag.Blocks = store.Blocks.ToList();
            ViewBag.Hours = store.Settings;
            ViewBag.OpenWindows = day.HasValue && !store.IsPersistent ? workflow.DemoOpenWindows(first.AddDays(day.Value - 1)) : null;
            return View(all.Where(b => string.IsNullOrEmpty(status) || b.Status == status).ToList());
        }
    }
    public IActionResult Catalog() { ViewBag.CatalogImages = (Func<string, Guid, string?>)((kind, id) => imageService.GetImageUrl(kind, id)); ViewBag.CatalogPreviewImages = (Func<string, Guid, string?>)((kind, id) => imageService.GetImageUrl(kind, id, "preview")); lock (store.Gate) { ViewBag.AddOns = store.AddOns.ToList(); return View(store.Services.ToList()); } }
    [HttpPost, RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> SaveCatalog(Guid? id, string kind, string name, string? description, decimal price, int minutes, bool active, string? visualWord = "", string? category = "", IFormFile? image = null, bool removeImage = false, IFormFile? previewImage = null, bool removePreviewImage = false, decimal? groupPrice = null, int groupMinimumPeople = 4, int? maxPerPackage = null)
    {
        byte[]? imageBytes = null;
        if (!removeImage && image != null && image.Length > 0)
        {
            using var memory = new MemoryStream();
            await image.CopyToAsync(memory);
            imageBytes = memory.ToArray();
        }
        byte[]? previewBytes = null;
        if (!removePreviewImage && previewImage != null && previewImage.Length > 0)
        {
            using var previewMemory = new MemoryStream();
            await previewImage.CopyToAsync(previewMemory);
            previewBytes = previewMemory.ToArray();
        }
        lock (store.Gate)
        {
            if (kind != "service" && kind != "addon") return BadRequest();
            if (!ModelState.IsValid || Money.Round(price) != price || (kind == "service" && groupPrice.HasValue && (groupPrice <= 0 || groupPrice > price || Money.Round(groupPrice.Value) != groupPrice || groupMinimumPeople < 2 || groupMinimumPeople > BookingPricing.MaximumPeople)))
            { TempData["Error"] = "Enter a valid group rate no higher than the regular rate, a threshold of 2–20 people, and prices with at most two decimal places."; return RedirectToAction("Catalog"); }
            if ((visualWord ?? "").Length > 40 || (category ?? "").Length > 60)
            { TempData["Error"] = "Overlay word must be 40 characters or fewer and category 60 characters or fewer."; return RedirectToAction("Catalog"); }
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || price < 0 || price > 1000000 || minutes < 0 || minutes > 480 || (kind == "service" && (price <= 0 || minutes <= 0)))
            { TempData["Error"] = "Enter a name, valid price and duration. Services need a positive price and duration."; return RedirectToAction("Catalog"); }
            if (kind == "service" && maxPerPackage is < 1 or > BookingPricing.MaximumPeople) { TempData["Error"] = $"Max per package must be between 1 and {BookingPricing.MaximumPeople}, or empty for no limit."; return RedirectToAction("Catalog"); }
            var list = kind == "service" ? store.Services : store.AddOns;
            var item = id.HasValue ? list.FirstOrDefault(x => x.Id == id) : new CatalogItem();
            if (item == null) return NotFound();
            if (!Request.Form.ContainsKey("groupPrice")) { groupPrice = item.GroupPrice; groupMinimumPeople = item.GroupMinimumPeople; }
            if (kind == "service" && groupPrice > price) { TempData["Error"] = "The regular price cannot be lower than the existing group price. Update the group rate first."; return RedirectToAction("Catalog"); }
            // Editing never un-archives: an archived item keeps its booking history and stays
            // hidden from customers. New items start not archived (the CatalogItem default).
            item.Name = name.Trim(); item.Description = description ?? ""; item.Price = price; item.Minutes = minutes; item.Active = active && !item.Archived;
            item.VisualWord = (visualWord ?? "").Trim();
            item.GroupPrice = kind == "service" ? groupPrice : null;
            item.GroupMinimumPeople = groupMinimumPeople;
            if (!Request.Form.ContainsKey("maxPerPackage")) maxPerPackage = item.MaxPerPackage; // forms without the field keep the limit
            item.MaxPerPackage = kind == "service" ? maxPerPackage : null;
            item.Category = string.IsNullOrWhiteSpace(category) ? "Signature" : category.Trim();
            try { store.SaveCatalog(kind, item, UserId); }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; return RedirectToAction("Catalog"); }
            if (store is DemoStore catalogDemo) catalogDemo.RecordAdminActivity(UserId, id.HasValue ? "Catalog item updated" : "Catalog item created", kind == "service" ? "Service" : "Add-on", item.Id);
            store.RecordAudit(UserId, $"Saved {kind}: {item.Name}");
            TempData["Success"] = "Catalog saved. Existing bookings keep their original prices.";
            try
            {
                if (removeImage)
                {
                    imageService.Remove(kind, item.Id);
                    store.RecordAudit(UserId, $"Removed {kind} image: {item.Name}");
                }
                else if (imageBytes != null)
                {
                    imageService.Save(kind, item.Id, imageBytes);
                    store.RecordAudit(UserId, $"Updated {kind} image: {item.Name}");
                }
                if (removePreviewImage)
                {
                    imageService.Remove(kind, item.Id, "preview");
                    store.RecordAudit(UserId, $"Removed {kind} preview image: {item.Name}");
                }
                else if (previewBytes != null)
                {
                    imageService.Save(kind, item.Id, previewBytes, "preview");
                    store.RecordAudit(UserId, $"Updated {kind} preview image: {item.Name}");
                }
            }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            return RedirectToAction("Catalog");
        }
    }
    [HttpPost]
    public IActionResult SaveGroupPricing(Guid id, decimal? groupPrice, int groupMinimumPeople = 4)
    {
        lock (store.Gate)
        {
            var item = store.Services.FirstOrDefault(s => s.Id == id);
            if (item == null) return NotFound();
            if (!ModelState.IsValid || groupMinimumPeople < 2 || groupMinimumPeople > BookingPricing.MaximumPeople || (groupPrice.HasValue && (groupPrice <= 0 || groupPrice > item.Price || Money.Round(groupPrice.Value) != groupPrice)))
                TempData["Error"] = "Group price must be positive, at most the regular price, and have at most two decimals. Choose a threshold of 2–20 people.";
            else
            {
                item.GroupPrice = groupPrice; item.GroupMinimumPeople = groupMinimumPeople;
                try { store.SaveCatalog("service", item, UserId); }
                catch (InvalidOperationException e) { TempData["Error"] = e.Message; return RedirectToAction("Catalog"); }
                if (store is DemoStore catalogDemo) catalogDemo.RecordAdminActivity(UserId, "Group pricing updated", "Service", item.Id);
                store.RecordAudit(UserId, $"Updated group pricing: {item.Name}");
                TempData["Success"] = "Group pricing updated. Existing bookings retain their price snapshots.";
            }
            return RedirectToAction("Catalog");
        }
    }
    [HttpPost]
    public IActionResult DeleteCatalog(Guid id, string kind)
    {
        lock (store.Gate)
        {
            var list = kind == "service" ? store.Services : store.AddOns;
            var item = list.FirstOrDefault(x => x.Id == id); if (item == null) return NotFound();
            if (kind != "service" && kind != "addon") return BadRequest();
            try
            {
                if (store.Bookings.Any(b => b.Service.ItemId == id || b.AddOns.Any(a => a.ItemId == id)))
                { item.Active = false; item.Archived = true; store.SaveCatalog(kind, item, UserId); TempData["Success"] = "Item archived to preserve booking history."; }
                else
                {
                    store.DeleteCatalog(kind, item, UserId);
                    // PostgreSQL rechecks references under a row lock and may archive
                    // a just-booked item instead; its pictures must then be retained.
                    var retained = (kind == "service" ? store.Services : store.AddOns).Any(x => x.Id == id);
                    if (!retained) { imageService.Remove(kind, item.Id); imageService.Remove(kind, item.Id, "preview"); }
                    TempData["Success"] = retained ? "Item archived to preserve booking history." : "Unused item deleted.";
                }
                if (store is DemoStore catalogDemo) catalogDemo.RecordAdminActivity(UserId, "Catalog item archived or removed", kind == "service" ? "Service" : "Add-on", item.Id);
            }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            return RedirectToAction("Catalog");
        }
    }
    public IActionResult Calendar(int? year, int? month)
    {
        return RedirectToAction(nameof(Appointments), new { mode = "calendar", year, month });
    }
    public IActionResult Settings() { lock (store.Gate)
    {
        // A misconfigured artist must not take the whole page down: only the location card says so.
        try { ViewBag.ArtistLocation = store.GetArtistLocation(); ViewBag.ArtistNow = store.Now; }
        catch (Exception e)
        {
            ViewBag.ArtistLocation = null;
            // Logged so the server log shows why the card says the location is unavailable.
            HttpContext?.RequestServices?.GetService<ILogger<AdminController>>()?.LogError(e, "Artist location could not be loaded for Settings.");
        }
        return View(store.Settings);
    }
    }
    public const string TravelSpeedMessage = "Travel speed must be between 5 and 60 km/h.";
    public const string NoHomeBaseMessage = "Current location cleared. No home base is saved yet: place the pin on the artist's home and press \"Set as home base\".";
    public const string TravelMinimumMessage = "Minimum travel must be between 0 and 180 minutes.";
    // The owner saves where the artist is based (home) or is right now (current); backHome clears the current pin.
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,SuperAdmin")]
    public IActionResult SetArtistHome(decimal? latitude, decimal? longitude)
    {
        lock (store.Gate)
        {
            try
            {
                if (latitude is not { } lat || longitude is not { } lng) throw new InvalidOperationException(BookingLocation.ArtistMessage);
                store.SetArtistHome(lat, lng); store.RecordAudit(UserId, "Set the artist's home base.");
                TempData["Success"] = "Home base saved. Travel estimates use it when no current location is set.";
            }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            return RedirectToAction("Settings");
        }
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,SuperAdmin")]
    public IActionResult SetArtistCurrent(decimal? latitude, decimal? longitude, bool backHome = false)
    {
        lock (store.Gate)
        {
            try
            {
                if (backHome)
                {
                    var homeSaved = store.GetArtistLocation().HomeLat.HasValue;
                    store.SetArtistCurrent(null, null);
                    store.RecordAudit(UserId, homeSaved ? "Moved the artist back to home base." : "Cleared the artist's current location (no home base saved).");
                    TempData["Success"] = homeSaved ? "The artist is back at home base." : NoHomeBaseMessage;
                }
                else
                {
                    if (latitude is not { } lat || longitude is not { } lng) throw new InvalidOperationException(BookingLocation.ArtistMessage);
                    store.SetArtistCurrent(lat, lng); store.RecordAudit(UserId, "Updated the artist's current location.");
                    TempData["Success"] = "Current location saved. Today's travel estimates start from here.";
                }
            }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            return RedirectToAction("Settings");
        }
    }
    [HttpPost]
    public IActionResult Settings(int openHour, int closeHour, int bufferMinutes, decimal travelFee, decimal downPaymentPercent, string serviceAreas, string? paymentInstructions, bool confirmAffected = false, decimal? travelSpeedKmh = null, int? travelMinimumMinutes = null, int? earlyRequestStartHour = null, int? afterHoursEndHour = null)
    {
        lock (store.Gate)
        {
            var speed = travelSpeedKmh ?? store.Settings.TravelSpeedKmh; var minimum = travelMinimumMinutes ?? store.Settings.TravelMinimumMinutes;
            // Phase 6: empty means "same as opening / closing"; only the SuperAdmin changes hours.
            var hoursIssue = BookingWindow.ValidateSettings(openHour, closeHour, earlyRequestStartHour, afterHoursEndHour);
            if (hoursIssue != null) TempData["Error"] = hoursIssue;
            else if (!Roles.IsSuperAdmin(User) && (earlyRequestStartHour != store.Settings.EarlyRequestStartHour || afterHoursEndHour != store.Settings.AfterHoursEndHour)) TempData["Error"] = "Only the SuperAdmin can change the early booking or latest end hours.";
            else if (speed < 5 || speed > 60) TempData["Error"] = TravelSpeedMessage;
            else if (minimum < 0 || minimum > 180) TempData["Error"] = TravelMinimumMessage;
            else if (!ModelState.IsValid || openHour < 0 || closeHour > 24 || openHour >= closeHour || bufferMinutes < 0 || bufferMinutes > 180 || travelFee < 0 || Money.Round(travelFee) != travelFee || downPaymentPercent is < 1 or > 100 || decimal.Round(downPaymentPercent) != downPaymentPercent || string.IsNullOrWhiteSpace(serviceAreas)) TempData["Error"] = "Enter valid hours, buffer, travel fee, down payment percentage (whole numbers 1–100) and service areas.";
            else if (!Roles.IsSuperAdmin(User) && downPaymentPercent != store.Settings.DownPaymentPercent) TempData["Error"] = "Only the SuperAdmin can change the down payment percentage.";
            else if (!Roles.IsSuperAdmin(User) && (openHour != store.Settings.OpenHour || closeHour != store.Settings.CloseHour || bufferMinutes != store.Settings.BufferMinutes)) TempData["Error"] = "Only the SuperAdmin can change service hours or the preparation buffer.";
            else if (!store.IsPersistent && store.Settings.EarlyFeeEnabled && openHour <= store.Settings.EarlyStartHour) TempData["Error"] = "Opening hour must be later than the configured early start.";
            else if (!store.IsPersistent && store.Settings.LateFeeEnabled && (closeHour > store.Settings.LateEndHour || openHour > store.Settings.LateStartHour)) TempData["Error"] = "Regular opening must not be later than the late fee boundary, and regular closing must not be later than final closing.";
            else if (workflow.BookingsOutsideHours(openHour, closeHour, earlyRequestStartHour, afterHoursEndHour) is { Count: > 0 } outside && !confirmAffected)
                TempData["Error"] = $"{Upcoming(outside)} {(outside.Count == 1 ? "falls" : "fall")} outside the new hours: {References(outside)}. Nothing was saved. Tick \"Save anyway\" to change the hours; these bookings keep their booked times.";
            else
            {
                var affected = workflow.BookingsOutsideHours(openHour, closeHour, earlyRequestStartHour, afterHoursEndHour);
                var s = store.Settings; var oldPercent = s.DownPaymentPercent; s.OpenHour = openHour; s.CloseHour = closeHour; s.EarlyRequestStartHour = earlyRequestStartHour; s.AfterHoursEndHour = afterHoursEndHour; s.BufferMinutes = bufferMinutes; s.TravelFee = travelFee; s.TravelSpeedKmh = speed; s.TravelMinimumMinutes = minimum; s.DownPaymentPercent = downPaymentPercent; s.ServiceAreas = serviceAreas; s.PaymentInstructions = paymentInstructions ?? "";
                var saved = false;
                try { store.SaveSettings(s, UserId); saved = true; if (store is DemoStore settingsDemo) settingsDemo.RecordAdminActivity(UserId, "Business settings updated", "Settings", Guid.Empty); TempData["Success"] = "Settings saved. Existing appointments and prices are unchanged."; }
                catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
                if (saved && oldPercent != downPaymentPercent) store.RecordAudit(UserId, $"Changed down payment policy from {oldPercent:N0}% to {downPaymentPercent:N0}%.");
                if (saved && affected.Count > 0)
                {
                    TempData["Success"] = $"Settings saved. {Upcoming(affected)} {(affected.Count == 1 ? "is" : "are")} outside the new hours ({References(affected)}). They keep their booked times; contact the customers if anything must change.";
                    store.RecordAudit(UserId, $"Changed service hours with {affected.Count} upcoming booking(s) outside them: {References(affected)}.");
                }
            }
            return RedirectToAction("Settings");
        }
    }
    private static string Upcoming(IReadOnlyCollection<Booking> bookings) => $"{bookings.Count} upcoming {(bookings.Count == 1 ? "booking" : "bookings")}";
    private static string References(IEnumerable<Booking> bookings)
    {
        var list = bookings.Select(b => b.Reference).ToList();
        return string.Join(", ", list.Take(5)) + (list.Count > 5 ? $" and {list.Count - 5} more" : "");
    }
    [HttpPost, Authorize(Roles = "SuperAdmin")]
    public IActionResult Block(DateTime from, DateTime to, string reason)
    {
        lock (store.Gate)
        {
            if (!ModelState.IsValid || from >= to || string.IsNullOrWhiteSpace(reason) || store.Bookings.Any(b => workflow.HoldsReservation(b) && from < b.End.AddMinutes(b.BufferMinutesSnapshot ?? store.Settings.BufferMinutes) && to > b.Start)) TempData["Error"] = "Enter a valid block that does not overlap a reserved appointment.";
            else
            {
                try { var block = new AvailabilityBlock(Guid.NewGuid(), from, to, reason); store.AddBlock(block, UserId); if (store is DemoStore calendarDemo) calendarDemo.RecordAdminActivity(UserId, "Unavailable time added", "Schedule", block.Id); TempData["Success"] = "Unavailable time added."; }
                catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
            }
            return RedirectToAction("Calendar");
        }
    }
    [HttpPost, Authorize(Roles = "SuperAdmin")]
    public IActionResult Unblock(Guid id)
    {
        lock (store.Gate)
        {
            try { var existed = store.Blocks.Any(b => b.Id == id); store.RemoveBlock(id, UserId); if (existed && store is DemoStore calendarDemo) calendarDemo.RecordAdminActivity(UserId, "Unavailable time removed", "Schedule", id); }
            catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        }
        return RedirectToAction("Calendar");
    }
    [HttpPost, Authorize(Roles = "SuperAdmin")]
    public IActionResult FeeSettings(bool earlyFeeEnabled, int earlyStartHour, decimal earlyFee, int lateEndHour, decimal lateFee, bool lateFeeEnabled = false, int? lateStartHour = null)
    {
        lock (store.Gate)
        {
            if (store.IsPersistent) TempData["Error"] = "Fee settings are not available in persistent storage.";
            else if (!ModelState.IsValid || earlyStartHour < 0 || earlyStartHour > 23 || lateEndHour < 1 || lateEndHour > 24 || (lateStartHour.HasValue && (lateStartHour < 0 || lateStartHour > 23)) || earlyFee < 0 || lateFee < 0 || earlyFee > 1000000 || lateFee > 1000000 || Money.Round(earlyFee) != earlyFee || Money.Round(lateFee) != lateFee || (earlyFeeEnabled && (earlyFee <= 0 || earlyStartHour >= store.Settings.OpenHour)) || (lateFeeEnabled && (lateFee <= 0 || !lateStartHour.HasValue || lateStartHour < store.Settings.OpenHour || lateStartHour >= lateEndHour || lateEndHour < store.Settings.CloseHour)))
                TempData["Error"] = "Enter valid peso amounts and hours: late fee start must be at or after opening and before final closing; final closing must be at or after regular closing.";
            else
            {
                var s = store.Settings;
                s.EarlyFeeEnabled = earlyFeeEnabled; s.EarlyStartHour = earlyStartHour; s.EarlyFee = earlyFee; s.LateStartHour = lateStartHour; s.LateEndHour = lateEndHour; s.LateFee = lateFee; s.LateFeeEnabled = lateFeeEnabled;
                store.RecordAudit(UserId, "Updated Demo early/late fee policy.");
                TempData["Success"] = "Demo fee settings updated for new bookings. Settings reset on restart.";
            }
            return RedirectToAction("Settings");
        }
    }
    public IActionResult Reports(DateTime? from, DateTime? to)
    {
        if (!ModelState.IsValid || (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date)) return BadRequest("Choose a valid date range.");
        lock (store.Gate)
        {
            var items = store.Bookings.Where(b => (!from.HasValue || b.Start.Date >= from.Value.Date) && (!to.HasValue || b.Start.Date <= to.Value.Date)).ToList();
            var ids = items.Select(b => b.Id).ToHashSet();
            ViewBag.Payments = store.Payments.Where(p => ids.Contains(p.BookingId)).ToList();
            ViewBag.From = from?.ToString("yyyy-MM-dd"); ViewBag.To = to?.ToString("yyyy-MM-dd");
            return View(items);
        }
    }
    public IActionResult Payments() { lock (store.Gate) { var visible = store.Bookings.Where(b => workflow.CanManageBooking(b, UserId)).ToList(); ViewBag.Payments = store.Payments.Where(p => visible.Any(b => b.Id == p.BookingId)).ToList(); return View(visible); } }
}
