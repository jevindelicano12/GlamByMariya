using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace GlamByMariya.Infrastructure.Services;

public class BookingService(IStudioStore store, IServiceRepository services, IAddOnRepository addons, IBookingRepository bookings, IPaymentRepository payments)
{
    public decimal Paid(Booking b) => store.IsPersistent ? b.VerifiedPaid : payments.Payments.Where(p => p.BookingId == b.Id && p.Verified).Sum(p => p.Amount);
    public void CheckReference(string method, [NotNull] string? reference, Guid? excludePayment = null)
    {
        ValidatePayment(method, reference);
        if (payments.Payments.Any(p => p.Id != excludePayment && p.Method == method && p.Reference.Trim().Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This payment reference has already been submitted. Check your appointments instead of paying again.");
    }
    public BookingQuote Quote(Guid serviceId, Guid[] addOnIds, int people = 1, string package = "auto", DateTime? start = null)
    {
        lock (store.Gate)
        {
            if (package == "auto" && people != 1) throw new InvalidOperationException("Individual bookings must have exactly one person.");
            var service = services.Services.FirstOrDefault(s => s.Id == serviceId && s.Active && !s.Archived) ?? throw new InvalidOperationException("Choose an active service.");
            var selected = SelectAddOns(addOnIds, people);
            var earlyFee = !store.IsPersistent && start.HasValue ? EarlyFeeAt(start.Value, store.Settings) : 0;
            var quote = BookingPricing.Calculate(service, selected, people, store.Settings.TravelFee, package, store.Settings.DownPaymentPercent, earlyFee);
            var lateFee = !store.IsPersistent && start.HasValue ? LateFeeAt(start.Value, quote.DurationMinutes, package, store.Settings) : 0;
            return lateFee == 0 ? quote : BookingPricing.Calculate(service, selected, people, store.Settings.TravelFee, package, store.Settings.DownPaymentPercent, earlyFee, lateFee);
        }
    }
    // Demo-only pricing; persistent booking remains separately guarded.
    public MixedBookingQuote QuoteMixed(IReadOnlyList<MixedPersonSelection>? people, DateTime? start = null)
    {
        if (store.IsPersistent) throw new InvalidOperationException("Mixed-service bookings are not available in persistent storage.");
        lock (store.Gate)
        {
            if (people == null || people.Count is < 2 or > BookingPricing.MaximumPeople)
                throw new InvalidOperationException($"Choose between 2 and {BookingPricing.MaximumPeople} people.");
            var snapshots = new List<MixedPersonQuote>();
            foreach (var person in people)
            {
                if (person == null || person.ServiceId == Guid.Empty || person.AddOnIds == null)
                    throw new InvalidOperationException("Choose a service and valid add-ons for every person.");
                if (person.AddOnIds.Count > addons.AddOns.Count || person.AddOnIds.Distinct().Count() != person.AddOnIds.Count)
                    throw new InvalidOperationException("Each add-on may be selected once per person.");
                var service = services.Services.FirstOrDefault(s => s.Id == person.ServiceId && s.Active && !s.Archived)
                    ?? throw new InvalidOperationException("Choose an active service for every person.");
                var selected = SelectAddOns(person.AddOnIds.ToArray(), 1);
                var individual = BookingPricing.Calculate(service, selected, 1, store.Settings.TravelFee, "auto", store.Settings.DownPaymentPercent);
                var serviceSnapshot = new PriceSnapshot(service.Id, service.Name, service.Price, service.Minutes);
                var addonSnapshots = selected.Select(a => new PriceSnapshot(a.Id, a.Name, a.Price, a.Minutes)).ToArray();
                snapshots.Add(new MixedPersonQuote(serviceSnapshot, Array.AsReadOnly(addonSnapshots), individual.FinalTotal, individual.DurationMinutes));
            }
            var duration = checked(snapshots.Sum(p => p.DurationMinutes));
            var serviceTotal = snapshots.Sum(p => p.Service.Price);
            var addonTotal = snapshots.Sum(p => p.AddOns.Sum(a => a.Price));
            var early = start.HasValue ? EarlyFeeAt(start.Value, store.Settings) : 0;
            var late = start.HasValue ? LateFeeAt(start.Value, duration, "group", store.Settings) : 0;
            var total = serviceTotal + addonTotal + early + late;
            return new MixedBookingQuote(snapshots.AsReadOnly(), serviceTotal, addonTotal, 0, early, late, total,
                Money.Percentage(total, store.Settings.DownPaymentPercent), store.Settings.DownPaymentPercent, duration);
        }
    }
    // Service-only evidence; travel to or between appointments is not estimated.
    public MixedDateAlternatives FindMixedAlternatives(IReadOnlyList<MixedPersonSelection> people, DateTime after, int daysToCheck)
    {
        if (store.IsPersistent) throw new InvalidOperationException("Mixed-service bookings are not available in persistent storage.");
        if (daysToCheck is < 1 or > 366) throw new InvalidOperationException("Choose a search range between 1 and 366 days.");
        lock (store.Gate)
        {
            var today = store.Now.Date;
            var afterDate = after.Date;
            var lastMonth = new DateTime(today.Year, today.Month, 1).AddMonths(12);
            var lastDate = lastMonth.AddMonths(1).AddDays(-1);
            if (afterDate < today || afterDate >= lastDate) throw new InvalidOperationException("Choose a date within the available booking window.");
            var quote = QuoteMixed(people);
            var count = Math.Min(daysToCheck, (lastDate - afterDate).Days);
            var end = afterDate.AddDays(count);
            var found = new List<AvailableDate>();
            var checkedThrough = afterDate;
            for (var first = new DateTime(afterDate.Year, afterDate.Month, 1); first <= end; first = first.AddMonths(1))
            {
                var calendar = GetAvailability(first, quote.DurationMinutes);
                foreach (var day in calendar.Days)
                {
                    var date = DateTime.ParseExact(day.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    if (date <= afterDate || date > end) continue;
                    checkedThrough = date;
                    if (day.AvailableCount > 0 && day.Slots.Any(slot => slot.Available)) found.Add(day);
                    if (found.Count == 3) return new MixedDateAlternatives(found.AsReadOnly(), checkedThrough, (checkedThrough - afterDate).Days);
                }
            }
            return new MixedDateAlternatives(found.AsReadOnly(), checkedThrough, (checkedThrough - afterDate).Days);
        }
    }
    private static decimal EarlyFeeAt(DateTime start, BusinessSettings settings) => settings.EarlyFeeEnabled && start.TimeOfDay >= TimeSpan.FromHours(settings.EarlyStartHour) && start.TimeOfDay < TimeSpan.FromHours(settings.OpenHour) ? settings.EarlyFee : 0;
    private static decimal LateFeeAt(DateTime start, int duration, string package, BusinessSettings settings) =>
        settings.LateFeeEnabled && settings.LateStartHour.HasValue && start.TimeOfDay >= TimeSpan.FromHours(settings.OpenHour)
        && (start.TimeOfDay >= TimeSpan.FromHours(settings.LateStartHour.Value)
            || (package == "group" && start.TimeOfDay + TimeSpan.FromMinutes(duration) > TimeSpan.FromHours(settings.CloseHour)))
            ? settings.LateFee : 0;
    private List<CatalogItem> SelectAddOns(Guid[] ids, int people)
    {
        if (people < 1 || people > BookingPricing.MaximumPeople || ids.Length > addons.AddOns.Count * people)
            throw new InvalidOperationException("Choose valid add-on quantities.");
        var groups = ids.GroupBy(id => id).ToList();
        if (groups.Any(g => g.Count() > (store.IsPersistent && !PreparedPersistentBooking.Enabled ? 1 : people))) throw new InvalidOperationException("Add-on quantity cannot exceed the number of people.");
        return groups.SelectMany(g => Enumerable.Repeat(addons.AddOns.FirstOrDefault(a => a.Id == g.Key && a.Active && !a.Archived) ?? throw new InvalidOperationException("An add-on is no longer available."), g.Count())).ToList();
    }
    private void UpdatePaymentStatus(Booking b)
    {
        b.VerifiedPaid = Paid(b);
        b.PaymentStatus = b.RefundDue > b.Refunded ? "Refund Pending" : b.RefundDue > 0 ? "Refunded" : b.VerifiedPaid >= b.Total ? "Paid in Full" : b.VerifiedPaid >= b.RequiredDownPayment ? "Down Payment Verified" : "Pending Verification";
    }
    public void Notify(Booking b, string message, Guid actor)
    {
        store.Notifications.Add(new(b.CustomerId, $"{b.Reference}: {message}", DateTime.Now));
        foreach (var admin in store.Users.Where(u => u.Role is "Admin" or "SuperAdmin")) store.Notifications.Add(new(admin.Id, $"{b.Reference}: {message}", DateTime.Now));
        store.AuditLogs.Add(new(actor, $"{b.Reference}: {message}", DateTime.Now));
    }
    public void CheckSlot(DateTime start, int duration, Guid? exclude = null)
    {
        lock (store.Gate)
        {
            var issue = SlotIssue(start, duration, store.Now, exclude);
            if (issue != null) throw new InvalidOperationException(issue);
        }
    }
    public bool HoldsReservation(Booking booking) => booking.Status is "Confirmed" or "Pending Payment";
    // Demo-only service/preparation windows; routing and travel are not calculated.
    public IReadOnlyList<ScheduleWindow> DemoOpenWindows(DateTime date)
    {
        if (store.IsPersistent) throw new InvalidOperationException("Persistent reservation availability is not verified.");
        lock (store.Gate)
        {
            var open = date.Date.AddHours(store.Settings.EarlyFeeEnabled ? store.Settings.EarlyStartHour : store.Settings.OpenHour);
            var close = date.Date.AddHours(store.Settings.LateFeeEnabled ? store.Settings.LateEndHour : store.Settings.CloseHour);
            if (open < store.Now) open = store.Now;
            if (open >= close) return [];
            var occupied = bookings.Bookings.Where(HoldsReservation)
                .Select(b => new ScheduleWindow(b.Start, b.End.AddMinutes(b.BufferMinutesSnapshot ?? store.Settings.BufferMinutes)))
                .Concat(store.Blocks.Select(b => new ScheduleWindow(b.From, b.To)))
                .Where(x => x.From < close && x.To > open).OrderBy(x => x.From).ToList();
            var windows = new List<ScheduleWindow>();
            var cursor = open;
            foreach (var span in occupied)
            {
                if (span.From > cursor) windows.Add(new(cursor, span.From < close ? span.From : close));
                if (span.To > cursor) cursor = span.To;
                if (cursor >= close) break;
            }
            if (cursor < close) windows.Add(new(cursor, close));
            return windows;
        }
    }
    private string? SlotIssue(DateTime start, int duration, DateTime now, Guid? exclude,
        BusinessSettings? cachedSettings = null, IReadOnlyList<Booking>? cachedBookings = null, IReadOnlyList<AvailabilityBlock>? cachedBlocks = null,
        IReadOnlyList<ScheduleWindow>? cachedOccupied = null)
    {
        var settings = cachedSettings ?? store.Settings;
        if (duration <= 0 || duration > 1440) return "Choose a valid session duration.";
        if (start <= now) return "Choose a future appointment.";
        var buffer = (cachedBookings ?? bookings.Bookings).FirstOrDefault(b => b.Id == exclude)?.BufferMinutesSnapshot ?? settings.BufferMinutes;
        if (start.Date == DateTime.MaxValue.Date || start > DateTime.MaxValue.AddMinutes(-duration - buffer)) return "Choose a valid appointment date.";
        var end = start.AddMinutes(duration);
        var reservedEnd = end.AddMinutes(buffer);
        var opening = !store.IsPersistent && settings.EarlyFeeEnabled ? settings.EarlyStartHour : settings.OpenHour;
        var existing = (cachedBookings ?? bookings.Bookings).FirstOrDefault(b => b.Id == exclude);
        var lateEnabled = !store.IsPersistent && settings.LateFeeEnabled;
        var closing = lateEnabled ? settings.LateEndHour : settings.CloseHour;
        var endLimit = start.Date.AddHours(closing);
        if ((existing == null || existing.Start != start) && (start < start.Date.AddHours(opening) || reservedEnd > endLimit)) return $"Choose a time after {opening}:00 that allows your session and {buffer}-minute preparation buffer to finish by {closing}:00.";
        if ((cachedBlocks ?? store.Blocks).Any(b => start < b.To && reservedEnd > b.From)) return "The artist is unavailable during this time. Please choose another slot.";
        var occupied = cachedOccupied ?? (store is PublicStudioStore publicStore ? publicStore.OccupiedIntervals(start.Date.AddDays(-1), start.Date.AddDays(2), exclude) : null);
        if (occupied != null ? occupied.Any(b => start < b.To && reservedEnd > b.From) :
            (cachedBookings ?? bookings.Bookings).Any(b => b.Id != exclude && HoldsReservation(b) && start < b.End.AddMinutes(b.BufferMinutesSnapshot ?? settings.BufferMinutes) && reservedEnd > b.Start))
            return "This slot overlaps a reserved appointment. Please choose another time.";
        return null;
    }
    public int SelectedDuration(Guid serviceId, Guid[] addOnIds, int people = 1, string package = "auto")
    {
        lock (store.Gate)
        {
            return Quote(serviceId, addOnIds, people, package).DurationMinutes;
        }
    }
    public AvailabilityCalendar GetAvailability(DateTime month, int duration, Guid? exclude = null, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            var now = store.Now;
            var first = new DateTime(month.Year, month.Month, 1);
            var current = new DateTime(now.Year, now.Month, 1);
            if (first < current || first > current.AddMonths(12)) throw new InvalidOperationException("Choose a month within the next 12 months.");
            if (duration <= 0 || duration > 1440) throw new InvalidOperationException("Choose a valid session duration.");
            cancellationToken.ThrowIfCancellationRequested();
            var settings = store.Settings;
            // One read of each database-backed collection per request, not one read
            // per candidate time slot (up to thousands of slots each month).
            var booked = bookings.Bookings;
            var blocks = store.Blocks;
            var occupied = store is PublicStudioStore publicStore ? publicStore.OccupiedIntervals(first.AddDays(-1), first.AddMonths(1).AddDays(1), exclude) : null;
            var excluded = booked.FirstOrDefault(b => b.Id == exclude);
            var buffer = excluded?.BufferMinutesSnapshot ?? settings.BufferMinutes;
            var days = new List<AvailableDate>();
            for (var date = first; date < first.AddMonths(1); date = date.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slots = new List<AvailableTimeSlot>();
                if (date >= now.Date)
                {
                    var lateEnabled = !store.IsPersistent && settings.LateFeeEnabled;
                    var last = date.AddHours(lateEnabled ? settings.LateEndHour : settings.CloseHour).AddMinutes(-duration - buffer);
                    for (var start = date.AddHours(!store.IsPersistent && settings.EarlyFeeEnabled ? settings.EarlyStartHour : settings.OpenHour); start <= last; start = start.AddMinutes(15))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var issue = SlotIssue(start, duration, now, exclude, settings, booked, blocks, occupied);
                        slots.Add(new(start.ToString("yyyy-MM-ddTHH:mm"), start.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture), issue == null, issue));
                    }
                }
                var available = slots.Count(s => s.Available);
                var hasBookings = occupied != null ? occupied.Any(b => b.From < date.AddDays(1) && b.To > date) :
                    booked.Any(b => b.Id != exclude && HoldsReservation(b) && b.Start < date.AddDays(1) && b.End.AddMinutes(b.BufferMinutesSnapshot ?? settings.BufferMinutes) > date);
                var status = date < now.Date ? "past" : available == 0 ? (hasBookings ? "full" : "unavailable") : available == slots.Count ? "available" : "limited";
                days.Add(new(date.ToString("yyyy-MM-dd"), status, available, slots));
            }
            return new(now.ToString("yyyy-MM-dd"), first.ToString("yyyy-MM"), duration, buffer, days);
        }
    }
    public Booking Create(Guid userId, Guid serviceId, Guid[] addOnIds, DateTime start, string address, string area, decimal deposit, string method, string reference, string? proof, bool agreed, int people = 1, string package = "auto", bool travelAcknowledged = false, decimal? expectedTotal = null, decimal? expectedMinimum = null)
    {
        if (store is IPersistentStudioStore database)
        {
            if (!expectedTotal.HasValue || !expectedMinimum.HasValue) throw new InvalidOperationException("Review the current price before submitting.");
            if (!PreparedPersistentBooking.Enabled && addOnIds.Distinct().Count() != addOnIds.Length) throw new InvalidOperationException("Add-on quantities are not yet available in persistent storage.");
            return database.SubmitBooking(userId, serviceId, addOnIds, start, address, area, deposit, method, reference, proof, agreed, travelAcknowledged, people, package, expectedTotal, expectedMinimum);
        }
        lock (store.Gate)
        {
            var service = services.Services.FirstOrDefault(x => x.Id == serviceId && x.Active && !x.Archived) ?? throw new InvalidOperationException("Choose an active service.");
            var selected = SelectAddOns(addOnIds, people);
            if (!agreed || !travelAcknowledged) throw new InvalidOperationException("Read and accept the Transportation Fee Policy before submitting payment.");
            if (string.IsNullOrWhiteSpace(address) || address.Length < 8 || address.Length > 500) throw new InvalidOperationException("Enter your complete home-service address (8–500 characters).");
            if (!store.Settings.ServiceAreas.Split(',').Any(x => x.Trim().Equals(area, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("The address must be within our service area.");
            var quote = Quote(serviceId, addOnIds, people, package, start);
            if ((expectedTotal.HasValue && expectedTotal != quote.FinalTotal) || (expectedMinimum.HasValue && expectedMinimum != quote.RequiredDownPayment)) throw new InvalidOperationException("The booking price or minimum payment changed. Please review the updated quote before submitting.");
            CheckSlot(start, quote.DurationMinutes);
            var user = store.Users.First(x => x.Id == userId);
            var b = new Booking { CustomerId = userId, CustomerName = user.Name, Service = new(service.Id, service.Name, service.Price, service.Minutes), AddOns = selected.GroupBy(x => x.Id).Select(g => new PriceSnapshot(g.Key, g.First().Name, g.First().Price, g.First().Minutes, g.Count())).ToList(), Start = start, BufferMinutesSnapshot = store.Settings.BufferMinutes, Address = address.Trim(), Area = area, TravelFee = 0, EarlyFeeSnapshot = quote.EarlyFee, LateFeeSnapshot = quote.LateFee, TransportationPolicyAcknowledgedAt = store.Now, People = people, Package = package, AppliedPricePerPerson = quote.AppliedPricePerPerson, GroupPrice = quote.GroupPrice, GroupMinimumPeople = quote.GroupMinimumPeople, DownPaymentPercentSnapshot = store.Settings.DownPaymentPercent, RequiredDownPaymentSnapshot = quote.RequiredDownPayment };
            if (Money.Round(deposit) != deposit || deposit < quote.RequiredDownPayment || deposit > quote.FinalTotal) throw new InvalidOperationException($"Choose an amount from ₱{quote.RequiredDownPayment:N2} to ₱{quote.FinalTotal:N2}, with at most two decimal places.");
            if (method == "Cash") throw new InvalidOperationException("Use GCash, Maya or Bank Transfer for your down payment.");
            CheckReference(method, reference);
            bookings.Bookings.Add(b);
            payments.Payments.Add(new() { BookingId = b.Id, Amount = deposit, Method = method, Reference = reference.Trim(), ProofPath = proof });
            Notify(b, "Booking submitted. Your payment and appointment are awaiting review.", userId);
            return b;
        }
    }
    public static void ValidateExpectedQuote(BookingQuote quote, decimal? expectedTotal, decimal? expectedMinimum)
    {
        if ((expectedTotal.HasValue || expectedMinimum.HasValue) &&
            (!expectedTotal.HasValue || !expectedMinimum.HasValue || expectedTotal != quote.FinalTotal || expectedMinimum != quote.RequiredDownPayment))
            throw new InvalidOperationException("The booking price or minimum payment changed. Please review the updated quote before submitting.");
    }
    public static void ValidatePersistentPaymentReview(Payment? payment, Guid? expectedId, long? expectedRevision, string? expectedVersion, bool alreadyConfirmed)
    {
        const string stale = "The payment claim changed or could not be verified. Reload and inspect the latest payment proof before verifying.";
        if (expectedId == null || expectedId == Guid.Empty || expectedRevision == null || expectedRevision < 0 ||
            !uint.TryParse(expectedVersion, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            payment == null || payment.Id != expectedId || payment.ProofKey == null || !payment.PersistentReviewRevision.HasValue ||
            (alreadyConfirmed
                ? !payment.Verified || expectedRevision == long.MaxValue || payment.PersistentReviewRevision != expectedRevision + 1
                : payment.Verified || payment.PersistentReviewRevision != expectedRevision || payment.PersistentReviewVersion != expectedVersion))
            throw new InvalidOperationException(stale);
    }
    public static void ValidatePersistentCorrectionToken(long? revision, string? version)
    {
        if (revision == null || revision < 0 ||
            !uint.TryParse(version, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("The payment proof changed. Reload and submit the latest proof correction.");
    }
    public static void ValidatePayment(string method, [NotNull] string? reference)
    {
        if (!new[] { "GCash", "Maya", "Bank Transfer", "Cash" }.Contains(method) || string.IsNullOrWhiteSpace(reference) || reference.Length > 150) throw new InvalidOperationException("Choose a payment method and provide a valid reference.");
    }
    public bool CanManageBooking(Booking b, Guid actor)
    {
        lock (store.Gate) return store.Users.Any(u => u.Id == actor && u.IsActive &&
            (u.Role == Roles.SuperAdmin || (u.Role == Roles.Admin && b.MixedPeople.Count == 0)));
    }
    private void RecordDecision(Booking b, Guid actor, string action, string reason)
    {
        b.History.Add(new(actor, store.Users.First(u => u.Id == actor).Name, action, reason, store.Now, b.Status, b.Start, b.RequestedStart));
    }
    private void CheckMixedReschedule(Booking b, DateTime start)
    {
        if (b.MixedPeople.Count == 0) return;
        var calendar = GetAvailability(start, (int)(b.End - b.Start).TotalMinutes, b.Id);
        if (start.Ticks % TimeSpan.TicksPerMinute != 0 || !calendar.Days.SelectMany(d => d.Slots).Any(s => s.Start == start.ToString("yyyy-MM-ddTHH:mm") && s.Available))
            throw new InvalidOperationException("Choose a current available makeup start time within the booking window.");
    }
    public void Cancel(Booking b, Guid actor, bool confirmed, string? reason = null)
    {
        if (store.IsPersistent && b.MixedPeople.Count > 0) throw new InvalidOperationException("Persistent mixed booking operations are unavailable.");
        if (store is IPersistentStudioStore database) { database.CancelBooking(b.Id, actor, confirmed); return; }
        lock (store.Gate)
        {
            if (b.CustomerId != actor || !store.Users.Any(u => u.Id == actor && u.IsActive && u.Role == Roles.Customer) || b.Status != "Confirmed" || b.Start <= store.Now || !confirmed)
                throw new InvalidOperationException("Confirm the cancellation of a future confirmed appointment.");
            reason = (reason ?? "").Trim();
            if (reason.Length > 1000 || (b.MixedPeople.Count > 0 && reason.Length == 0)) throw new InvalidOperationException("Provide a cancellation reason, up to 1,000 characters.");
            if (reason.Length == 0) reason = "Customer requested cancellation.";
            b.CancellationFee = 200;
            b.RefundDue = Math.Max(0, Paid(b) - 200);
            b.Status = "Cancelled";
            b.RequestedStart = null;
            b.Note = reason;
            UpdatePaymentStatus(b);
            RecordDecision(b, actor, "Customer cancellation", reason);
            Notify(b, $"Cancelled. ₱200 cancellation fee; refund due ₱{b.RefundDue:N2}.", actor);
        }
    }
    public void VerifyCancelledReceipt(Booking b, Guid actor, Guid paymentId, int revision, bool reviewed)
    {
        if (store.IsPersistent) throw new InvalidOperationException("Receipt review is available in the isolated Demo only.");
        lock (store.Gate)
        {
            if (!store.Users.Any(u => u.Id == actor && u.IsActive && u.Role == Roles.SuperAdmin)) throw new InvalidOperationException("Only an active SuperAdmin can verify a cancelled booking receipt.");
            if (b.Status != "Cancelled" || !b.StaffCancelledAt.HasValue) throw new InvalidOperationException("Only business-cancelled bookings can use this receipt review.");
            var payment = payments.Payments.SingleOrDefault(p => p.BookingId == b.Id && p.Id == paymentId);
            if (payment == null || payment.Revision != revision) throw new InvalidOperationException("The payment claim changed. Reload before reviewing it.");
            if (payment.Verified) throw new InvalidOperationException("This receipt is already verified.");
            if (!reviewed || payment.ProofPath == null || !File.Exists(payment.ProofPath)) throw new InvalidOperationException("Inspect the available payment proof and confirm the actual transfer before verifying.");
            payment.Verified = true; payment.VerifiedBy = actor; payment.VerifiedAt = store.Now;
            UpdatePaymentStatus(b);
            RecordDecision(b, actor, "Cancelled booking receipt verified", "Receipt checked separately; cancellation and refund remain unchanged.");
            if (store is DemoStore demo) demo.RecordAdminActivity(actor, "Cancelled booking receipt verified", "Booking", b.Id);
            Notify(b, "Payment receipt verified for the cancelled appointment. Refund arrangements remain personal.", actor);
        }
    }
    public void AdminAction(Booking b, string action, Guid actor, string reason, decimal amount, string reference, bool confirmed = false, Guid? paymentId = null, long? reviewRevision = null, string? reviewVersion = null)
    {
        if (store.IsPersistent && b.MixedPeople.Count > 0) throw new InvalidOperationException("Persistent mixed booking operations are unavailable.");
        if (store.IsPersistent && (action is "cancel" or "complete")) throw new InvalidOperationException("Staff cancellation and manual completion are available in the isolated Demo only.");
        if (store.IsPersistent && (action is "verify" or "approve" or "proof"))
            throw new InvalidOperationException("Persistent payment review is unavailable until transaction-scoped authorization is verified.");
        if (store is IPersistentStudioStore database) { database.ManageBooking(b.Id, action, actor, reason, amount, reference, paymentId, reviewRevision, reviewVersion); return; }
        lock (store.Gate)
        {
            if (!CanManageBooking(b, actor)) throw new InvalidOperationException("This booking requires an active authorized staff account. Mixed-service management is SuperAdmin-only until granular grants are implemented.");
            reason = (reason ?? "").Trim();
            if (reason.Length > 1000) throw new InvalidOperationException("Use a reason of up to 1,000 characters.");
            switch (action)
            {
                case "verify":
                    if (b.Status == "Confirmed" && Paid(b) >= b.RequiredDownPayment)
                    {
                        if (b.MixedPeople.Count > 0) throw new InvalidOperationException("This payment is already verified and the appointment is confirmed. No second verification was recorded.");
                        return;
                    }
                    if (b.Status != "Pending Payment") throw new InvalidOperationException("Only pending down payments can be verified.");
                    var deposit = payments.Payments.SingleOrDefault(p => p.BookingId == b.Id) ?? throw new InvalidOperationException("No down payment was submitted.");
                    if (deposit.Amount < b.RequiredDownPayment || deposit.Amount > b.Total || Money.Round(deposit.Amount) != deposit.Amount) throw new InvalidOperationException("The submitted payment must be between the required minimum and the booking total.");
                    CheckReference(deposit.Method, deposit.Reference, deposit.Id);
                    // Validate everything before changing either payment or booking under the same lock.
                    if (!store.Settings.ServiceAreas.Split(',').Any(x => x.Trim().Equals(b.Area, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("This address is outside the configured service area.");
                    CheckSlot(b.Start, (int)(b.End - b.Start).TotalMinutes, b.Id);
                    deposit.Verified = true; deposit.VerifiedBy = actor; deposit.VerifiedAt = DateTime.Now;
                    b.Status = "Confirmed";
                    b.Note = "Submitted payment verified. Please have clean, moisturized skin and a well-lit space ready.";
                    break;
                case "approve":
                    if (b.Status == "Confirmed" && Paid(b) >= b.RequiredDownPayment) return;
                    if (b.Status != "Pending Payment" || Paid(b) < b.RequiredDownPayment || Paid(b) > b.Total) throw new InvalidOperationException("Verify at least the required down payment before confirming this booking.");
                    if (!store.Settings.ServiceAreas.Split(',').Any(x => x.Trim().Equals(b.Area, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("This address is no longer within the configured service areas. Reject or update coverage before approval.");
                    CheckSlot(b.Start, (int)(b.End - b.Start).TotalMinutes, b.Id);
                    b.Status = "Confirmed";
                    b.Note = "Please have clean, moisturized skin and a well-lit space ready.";
                    break;
                case "proof":
                    if (b.Status != "Pending Payment" || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide payment correction instructions for a pending booking.");
                    foreach (var p in payments.Payments.Where(p => p.BookingId == b.Id)) { p.Verified = false; p.VerifiedBy = null; p.VerifiedAt = null; }
                    b.Note = reason;
                    break;
                case "reject":
                    if (b.Status != "Pending Payment" || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide a reason to reject a pending booking.");
                    b.Status = "Rejected"; b.Note = reason; b.RefundDue = Paid(b); break;
                case "reschedule":
                    if (!store.Users.Any(u => u.Id == actor && u.IsActive && u.Role == Roles.SuperAdmin)) throw new InvalidOperationException("Only SuperAdmin can approve a reschedule.");
                    var consent = b.PendingReschedule ?? throw new InvalidOperationException("Customer consent to a current quote is required.");
                    if (b.Status != "Confirmed" || b.RequestedStart != consent.Quote.ProposedStart || consent.CustomerId != b.CustomerId) throw new InvalidOperationException("This request is no longer valid.");
                    var approvedQuote = ReschedulePrice(b, consent.Quote.ProposedStart, consent.Quote.Id);
                    if (approvedQuote != consent.Quote) throw new InvalidOperationException("The quote changed. The customer must review and consent again.");
                    b.ApprovedReschedules.Add(new(approvedQuote, consent, actor, store.Now));
                    b.Start = approvedQuote.ProposedStart; b.RequestedStart = null; b.PendingReschedule = null; b.RescheduleOffer = null;
                    b.Note = "Your rescheduling request has been approved. If your updated appointment has additional charges or payment differences, please coordinate directly with Glam by Mariya for payment or refund arrangements.";
                    break;
                case "decline":
                    if (b.Status != "Confirmed" || b.RequestedStart == null || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide a reason for declining the reschedule.");
                    b.RequestedStart = null; b.PendingReschedule = null; b.RescheduleOffer = null; b.Note = reason; break;
                case "complete":
                    if (!store.Users.Any(u => u.Id == actor && u.IsActive && u.Role == Roles.SuperAdmin)) throw new InvalidOperationException("Only an active SuperAdmin can complete an appointment.");
                    if (!confirmed) throw new InvalidOperationException("Confirm that all makeup services are finished before completing this appointment.");
                    if (b.Status != "Confirmed" || b.End > store.Now) throw new InvalidOperationException("Only confirmed appointments whose makeup services have ended can be marked completed.");
                    b.Status = "Completed"; b.CompletedBy = actor; b.CompletedAt = store.Now;
                    b.Note = "Your appointment was marked completed by Glam by Mariya. Payment status is recorded separately."; break;
                case "noshow":
                case "incomplete":
                    if (b.Status != "Confirmed" || b.Start > DateTime.Now || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Provide a reason for a past confirmed appointment.");
                    b.Status = action == "noshow" ? "No-show" : "Incomplete"; b.Note = reason; break;
                case "refund":
                    if (amount <= 0 || amount > b.RefundDue - b.Refunded || string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException("Enter a valid refund amount and transfer reference.");
                    b.Refunded += amount; b.RefundReference = reference; break;
                case "cancel":
                    if (!store.Users.Any(u => u.Id == actor && u.IsActive && u.Role == Roles.SuperAdmin)) throw new InvalidOperationException("Only an active SuperAdmin can cancel an appointment on behalf of the business.");
                    if (!confirmed) throw new InvalidOperationException("Confirm the cancellation before continuing.");
                    if (b.Status is not ("Pending Payment" or "Confirmed")) throw new InvalidOperationException("Only pending or confirmed appointments can be cancelled by the business.");
                    if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Enter a reason for cancelling this appointment.");
                    b.Status = "Cancelled"; b.StaffCancelledBy = actor; b.StaffCancelledAt = store.Now; b.StaffCancellationReason = reason;
                    b.RequestedStart = null; b.PendingReschedule = null; b.RescheduleOffer = null;
                    b.Note = "Glam by Mariya has cancelled this appointment. Mariya will contact you about verified payments and any applicable refund. Refund arrangements are handled personally.";
                    break;
                default: throw new InvalidOperationException("Unknown booking action.");
            }
            if (action is not ("cancel" or "complete")) UpdatePaymentStatus(b);
            RecordDecision(b, actor, action, reason);
            if (store is DemoStore demo) demo.RecordAdminActivity(actor, action switch
            {
                "verify" => "Payment verified and appointment confirmed",
                "approve" => "Appointment approved",
                "proof" => "Payment correction requested",
                "reject" => "Appointment rejected",
                "reschedule" => "Reschedule approved",
                "decline" => "Reschedule declined",
                "complete" => "Appointment completed",
                "cancel" => "Appointment cancelled by SuperAdmin",
                "noshow" => "Appointment marked no-show",
                "incomplete" => "Appointment marked incomplete",
                "refund" => "Refund recorded",
                _ => "Appointment updated"
            }, "Booking", b.Id);
            Notify(b, $"{action}: {b.Status}. {b.Note}", actor);
        }
    }
    public Booking CreateMixed(Guid userId, IReadOnlyList<MixedPersonSelection>? people, DateTime start, string address, string? area, decimal deposit, string method, string? reference, string? proof, bool agreed, bool travelAcknowledged, decimal expectedTotal, decimal expectedMinimum, Guid? submissionId = null, string? policyVersion = null, string? travelReminderVersion = null, string? imageFingerprint = null)
    {
        if (store.IsPersistent) throw new InvalidOperationException("Mixed-service bookings are not available in persistent storage.");
        lock (store.Gate)
        {
            if (!store.Users.Any(x => x.Id == userId && x.IsActive && x.Role == Roles.Customer)) throw new InvalidOperationException("Only an active customer can submit a booking.");
            var requestFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                userId, people = people?.Select(p => new { p.ServiceId, addons = p.AddOnIds.OrderBy(id => id).ToArray() }).ToArray(),
                start, address = address?.Trim(), area = area?.ToUpperInvariant(), deposit = deposit.ToString("G29", CultureInfo.InvariantCulture), method,
                reference = reference?.Trim(), proof = imageFingerprint == null ? "reference:" + proof?.Trim() : "image:" + imageFingerprint,
                agreed, travelAcknowledged, expectedTotal = expectedTotal.ToString("G29", CultureInfo.InvariantCulture), expectedMinimum = expectedMinimum.ToString("G29", CultureInfo.InvariantCulture), policyVersion, travelReminderVersion
            })));
            if (submissionId.HasValue)
            {
                var existing = bookings.Bookings.FirstOrDefault(b => b.CustomerId == userId && b.MixedSubmissionId == submissionId);
                if (existing != null)
                {
                    if (existing.MixedSubmission?.RequestFingerprint != requestFingerprint) throw new InvalidOperationException("Submission ID was already used for different booking details.");
                    return existing;
                }
            }
            if (policyVersion != store.Settings.PolicyVersion || travelReminderVersion != store.Settings.TravelReminderVersion)
                throw new InvalidOperationException("The booking policies changed. Reload and review them before submitting.");
            if (!agreed || !travelAcknowledged) throw new InvalidOperationException("Read and accept the Transportation Fee Policy before submitting payment.");
            if (string.IsNullOrWhiteSpace(proof)) throw new InvalidOperationException("Upload a payment screenshot to submit your booking.");
            if (string.IsNullOrWhiteSpace(address) || address.Length < 8 || address.Length > 500) throw new InvalidOperationException("Enter your complete home-service address (8–500 characters).");
            if (!store.Settings.ServiceAreas.Split(',').Any(x => x.Trim().Equals(area, StringComparison.OrdinalIgnoreCase)) || area is null) throw new InvalidOperationException("The address must be within our service area.");
            var quote = QuoteMixed(people, start);
            var calendar = GetAvailability(start, quote.DurationMinutes);
            if (!calendar.Days.SelectMany(d => d.Slots).Any(s => s.Start == start.ToString("yyyy-MM-ddTHH:mm") && s.Available)
                || start.Ticks % TimeSpan.TicksPerMinute != 0)
                throw new InvalidOperationException("This makeup start time is no longer available. Choose another available time.");
            if (expectedTotal != quote.FinalTotal || expectedMinimum != quote.RequiredDownPayment) throw new InvalidOperationException("The booking price or minimum payment changed. Please review the updated quote before submitting.");
            CheckSlot(start, quote.DurationMinutes);
            if (Money.Round(deposit) != deposit || deposit < quote.RequiredDownPayment || deposit > quote.FinalTotal) throw new InvalidOperationException($"Choose an amount from ₱{quote.RequiredDownPayment:N2} to ₱{quote.FinalTotal:N2}, with at most two decimal places.");
            if (method == "Cash") throw new InvalidOperationException("Use GCash, Maya or Bank Transfer for your down payment.");
            CheckReference(method, reference);
            var user = store.Users.First(x => x.Id == userId);
            var booking = new Booking {
                CustomerId = userId, CustomerName = user.Name, Service = quote.People[0].Service,
                MixedPeople = quote.People, AddOns = quote.People.SelectMany(p => p.AddOns).ToList(),
                MixedSubmissionId = submissionId,
                MixedSubmission = new(store.Settings.BufferMinutes, quote.EarlyFee, quote.LateFee, quote.FinalTotal,
                    quote.RequiredDownPayment, deposit, method, reference.Trim(), store.Now, policyVersion!, travelReminderVersion!, requestFingerprint),
                Start = start, BufferMinutesSnapshot = store.Settings.BufferMinutes, Address = address.Trim(), Area = area,
                EarlyFeeSnapshot = quote.EarlyFee, LateFeeSnapshot = quote.LateFee, TransportationPolicyAcknowledgedAt = store.Now,
                People = quote.People.Count, Package = "group", DownPaymentPercentSnapshot = quote.DownPaymentPercent,
                RequiredDownPaymentSnapshot = quote.RequiredDownPayment
            };
            bookings.Bookings.Add(booking);
            payments.Payments.Add(new() { BookingId = booking.Id, Amount = deposit, Method = method, Reference = reference.Trim(), ProofPath = proof });
            Notify(booking, "Booking submitted. Your payment and appointment are awaiting review.", userId);
            return booking;
        }
    }
    private RescheduleQuote ReschedulePrice(Booking b, DateTime start, Guid quoteId)
    {
        if (store.IsPersistent) throw new InvalidOperationException("This rescheduling policy is unavailable in persistent storage.");
        if (b.Status != "Confirmed" || b.Start <= store.Now) throw new InvalidOperationException("Only future confirmed appointments can be rescheduled.");
        var duration = (int)(b.End - b.Start).TotalMinutes;
        var calendar = GetAvailability(start, duration, b.Id);
        if (start.Ticks % TimeSpan.TicksPerMinute != 0 || !calendar.Days.SelectMany(d => d.Slots).Any(s => s.Start == start.ToString("yyyy-MM-ddTHH:mm") && s.Available)) throw new InvalidOperationException("Choose an available appointment time.");
        CheckSlot(start, duration, b.Id);
        var early = EarlyFeeAt(start, store.Settings); var late = LateFeeAt(start, duration, b.Package, store.Settings);
        return new(quoteId, b.Id, b.ApprovedReschedules.Count, b.Start, start, duration, b.BufferMinutesSnapshot ?? store.Settings.BufferMinutes, b.InitialBookingTotal, b.Total, b.InitialBookingTotal - b.EarlyFeeSnapshot - b.LateFeeSnapshot + early + late, early, late);
    }
    public RescheduleQuote OfferReschedule(Booking b, Guid actor, DateTime start)
    {
        lock (store.Gate)
        {
            if (b.CustomerId != actor || !store.Users.Any(u => u.Id == actor && u.IsActive && u.Role == Roles.Customer)) throw new InvalidOperationException("Only the booking customer may request a reschedule.");
            return b.RescheduleOffer = ReschedulePrice(b, start, Guid.NewGuid());
        }
    }
    public void ConsentReschedule(Booking b, Guid actor, Guid quoteId, bool agreed)
    {
        lock (store.Gate)
        {
            if (b.CustomerId != actor || !store.Users.Any(u => u.Id == actor && u.IsActive && u.Role == Roles.Customer)) throw new InvalidOperationException("Only the booking customer may request a reschedule.");
            if (!agreed || b.RescheduleOffer is not { } offer || offer.Id != quoteId) throw new InvalidOperationException("Review and agree to the current rescheduling quote.");
            if (ReschedulePrice(b, offer.ProposedStart, offer.Id) != offer) throw new InvalidOperationException("The quote changed. Please review the updated total again.");
            var consent = new RescheduleConsent(offer, actor, store.Now);
            b.RescheduleConsents.Add(consent); b.PendingReschedule = consent; b.RequestedStart = offer.ProposedStart; b.RescheduleOffer = null;
            RecordDecision(b, actor, "Reschedule requested", $"Customer agreed to ₱{offer.ProposedTotal:N2}; previous total ₱{offer.CurrentTotal:N2}. Original reservation retained.");
            Notify(b, $"Reschedule requested for {offer.ProposedStart:MMM d, yyyy h:mm tt}. Original slot stays reserved until approval.", actor);
        }
    }
    public void Reschedule(Booking b, Guid actor, DateTime start)
    {
        if (store.IsPersistent && b.MixedPeople.Count > 0) throw new InvalidOperationException("Persistent mixed booking operations are unavailable.");
        if (store is IPersistentStudioStore database) { database.RequestReschedule(b.Id, actor, start); return; }
        lock (store.Gate)
        {
            if (b.CustomerId != actor || b.Status != "Confirmed" || b.Start <= store.Now)
                throw new InvalidOperationException("Only future confirmed appointments can be rescheduled.");
            if (EarlyFeeAt(start, store.Settings) != b.EarlyFeeSnapshot || LateFeeAt(start, (int)(b.End - b.Start).TotalMinutes, b.Package, store.Settings) != b.LateFeeSnapshot) throw new InvalidOperationException("This reschedule changes a booking fee. Contact the artist; fee-changing reschedules are not supported.");
            CheckMixedReschedule(b, start);
            CheckSlot(start, (int)(b.End - b.Start).TotalMinutes, b.Id);
            b.RequestedStart = start;
            RecordDecision(b, actor, "Reschedule requested", "Original reservation retained; proposed time is not reserved.");
            Notify(b, $"Reschedule requested for {start:MMM d, yyyy h:mm tt}. Original slot stays reserved until approval.", actor);
        }
    }
    public void CorrectProof(Booking b, Guid actor, string reference, string? proof, long? expectedPaymentRevision = null, string? expectedPaymentVersion = null)
    {
        if (store.IsPersistent && b.MixedPeople.Count > 0) throw new InvalidOperationException("Persistent mixed booking operations are unavailable.");
        if (store.IsPersistent)
        {
            ValidatePersistentCorrectionToken(expectedPaymentRevision, expectedPaymentVersion);
            throw new InvalidOperationException("Persistent payment correction is unavailable until transaction-scoped authorization is verified.");
        }
        lock (store.Gate)
        {
        var p = payments.Payments.First(x => x.BookingId == b.Id);
        if (b.CustomerId != actor || b.Status != "Pending Payment" || p.Verified)
            throw new InvalidOperationException("Only unverified pending deposits can be corrected.");
        CheckReference(p.Method, reference, p.Id);
        p.ProofHistory.Add(new(p.Reference, p.ProofPath, p.ProofKey, store.Now));
        p.Revision++;
        p.Reference = reference.Trim(); if (proof != null) p.ProofPath = proof;
        RecordDecision(b, actor, "Payment proof updated", "An unverified replacement claim was submitted for review.");
        Notify(b, "Customer updated payment proof for review.", actor);
        }
    }
}
