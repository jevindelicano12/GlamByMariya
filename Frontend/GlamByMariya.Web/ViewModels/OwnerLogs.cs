using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Data;

namespace GlamByMariya.Web.ViewModels;

// One row in Owner → Audit Logs / Transaction Logs. Times are business (Manila) time.
public sealed record OwnerLogEntry(
    DateTime At, string ActorName, string ActorRole, string Category, string Action,
    string Record, string? Amount, string Result, bool IsTransaction,
    IReadOnlyList<KeyValuePair<string, string>>? Archive = null);

public sealed record OwnerLogFilter(string Q, string Category, DateOnly? From, DateOnly? To, int PageSize = OwnerLogs.DefaultPageSize);

// One page of matching entries. Page is 1-based and already clamped to 1..TotalPages.
public sealed record OwnerLogPage(string Title, string Intro, string ActionName, bool Transactions,
    IReadOnlyList<string> Categories, OwnerLogFilter Filter, IReadOnlyList<OwnerLogEntry> Entries, int Total, int Page)
{
    public int TotalPages => Math.Max(1, (Total + Filter.PageSize - 1) / Filter.PageSize);
    public int FirstShown => Total == 0 ? 0 : (Page - 1) * Filter.PageSize + 1;
    public int LastShown => (Page - 1) * Filter.PageSize + Entries.Count;
}

// Builds the owner's log views from what the store already records:
//  • transactions — derived from bookings and payments, so they work in Demo and database mode alike;
//  • everything else — Demo admin activity, or the database audit_logs table.
public static class OwnerLogs
{
    // Rows per page the owner can choose; the default keeps a page short enough to scan.
    public static readonly int[] PageSizes = [10, 25, 50, 100];
    public const int DefaultPageSize = 25;
    // The most recent audit records read from the database for paging (older ones stay in the table).
    public const int MaxSourceRows = 5000;
    public static int NormalizePageSize(int? size) => size is { } s && PageSizes.Contains(s) ? s : DefaultPageSize;
    public static readonly string[] TransactionCategories = ["Booking", "Payment", "Refund", "Cancellation", "Completion"];

    private static readonly TimeZoneInfo Manila = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
    private static string Peso(decimal v) => "₱" + v.ToString("#,##0.00");

    public static List<OwnerLogEntry> Transactions(IStudioStore store)
    {
        var users = store.Users.ToDictionary(u => u.Id);
        string Name(Guid? id, string fallback) => id is { } v && users.TryGetValue(v, out var u) ? u.Name : fallback;
        string Role(Guid? id, string fallback) => id is { } v && users.TryGetValue(v, out var u) ? u.Role : fallback;
        var payments = store.Payments.ToLookup(p => p.BookingId);
        var list = new List<OwnerLogEntry>();
        foreach (var b in store.Bookings)
        {
            var reference = $"{b.Reference} · {b.CustomerName}";
            var created = b.CreatedAt ?? b.Start;
            list.Add(new(created, b.CustomerName, "Customer", "Booking", $"Booking placed: {b.Service.Name}" + (b.People > 1 ? $" · {b.People} people" : ""),
                reference, Peso(b.Total), b.Status, true));
            foreach (var p in payments[b.Id])
            {
                var status = p.Verified ? "Verified" : p.ReviewStatus ?? (b.Status is "Cancelled" or "Rejected" or "Expired" ? "Not paid" : "Pending");
                var at = p.VerifiedAt ?? b.PaidAt ?? created;
                var by = p.Verified && p.VerifiedBy is { } v ? Name(v, "Staff") : b.CustomerName;
                var role = p.Verified && p.VerifiedBy is { } r ? Role(r, "Admin") : "Customer";
                list.Add(new(at, by, role, "Payment", $"Payment {(p.Verified ? "verified" : "recorded")} · {p.Method}" + (string.IsNullOrWhiteSpace(p.Reference) ? "" : $" · Ref {p.Reference}"),
                    reference, Peso(p.Amount), status, true));
            }
            if (b.StaffCancelledAt is { } cancelledAt)
                list.Add(new(cancelledAt, Name(b.StaffCancelledBy, "Staff"), Role(b.StaffCancelledBy, "Admin"), "Cancellation",
                    "Booking cancelled by staff" + (string.IsNullOrWhiteSpace(b.StaffCancellationReason) ? "" : $": {b.StaffCancellationReason}"),
                    reference, b.CancellationFee > 0 ? Peso(b.CancellationFee) + " fee" : null, b.Status, true));
            if (b.CompletedAt is { } completedAt)
                list.Add(new(completedAt, Name(b.CompletedBy, "Staff"), Role(b.CompletedBy, "Admin"), "Completion", "Appointment marked completed",
                    reference, Peso(b.Total), b.Status, true));
            // Customer cancellations and other status changes recorded on the booking's own history (Demo mode).
            foreach (var h in b.History.Where(h => h.Action.Contains("cancel", StringComparison.OrdinalIgnoreCase) && b.StaffCancelledAt == null))
                list.Add(new(h.At, h.ActorName, users.TryGetValue(h.ActorId, out var hu) ? hu.Role : "Customer", "Cancellation", h.Action + (string.IsNullOrWhiteSpace(h.Reason) ? "" : $": {h.Reason}"),
                    reference, null, h.Status, true));
            if (b.Refunded > 0)
                list.Add(new(b.StaffCancelledAt ?? created, Name(b.StaffCancelledBy, "Staff"), Role(b.StaffCancelledBy, "Admin"), "Refund", "Refund sent" + (string.IsNullOrWhiteSpace(b.RefundReference) ? "" : $" · Ref {b.RefundReference}"),
                    reference, Peso(b.Refunded), "Sent", true));
            if (b.RefundDue > b.Refunded)
                list.Add(new(b.StaffCancelledAt ?? created, "System", "System", "Refund", "Refund owed to customer",
                    reference, Peso(b.RefundDue - b.Refunded), "Waiting", true));
        }
        return list;
    }

    // Everything: transactions plus administrative/system activity.
    public static List<OwnerLogEntry> All(IStudioStore store)
    {
        var users = store.Users.ToDictionary(u => u.Id);
        var list = Transactions(store);
        if (store is DemoStore demo)
        {
            foreach (var a in demo.AdminActivity())
                list.Add(new(TimeZoneInfo.ConvertTime(a.OccurredAt, Manila).DateTime, a.ActorName, a.Role, a.TargetType, a.Action,
                    a.TargetId == Guid.Empty ? "—" : $"{a.TargetType} {a.TargetId.ToString()[..8]}…", null, "Succeeded", false, a.Archive));
        }
        else
        {
            foreach (var log in store.RecentAuditLogs(MaxSourceRows).Reverse()) // oldest first, like the other sources
            {
                var (category, action) = Describe(log.Event);
                if (category is null) continue; // covered by the readable transaction rows above
                var known = users.TryGetValue(log.ActorId, out var u);
                list.Add(new(log.Created, known ? u!.Name : log.ActorId == Guid.Empty ? "System" : "Unknown user", known ? u!.Role : "System",
                    category, action, "—", null, "Recorded", false));
            }
        }
        return list;
    }

    // Database audit rows are "<table>: <INSERT|UPDATE|DELETE>" (triggers) or "application: <message>".
    // Returns (null, _) for booking/payment tables, which the Transaction rows already describe.
    private static (string? Category, string Action) Describe(string raw)
    {
        var i = raw.IndexOf(": ", StringComparison.Ordinal);
        var table = i < 0 ? "application" : raw[..i];
        var op = i < 0 ? raw : raw[(i + 2)..];
        if (table == "application") return (CategoryOfMessage(op), op);
        var verb = op switch { "INSERT" => "added", "UPDATE" => "updated", "DELETE" => "removed", _ => op.ToLowerInvariant() };
        return table switch
        {
            "reservations" or "payments" or "refunds" or "reservation_closures" or "payment_revisions" or "payment_adjustments" or "reservation_history" => (null, ""),
            "customers" => ("User account", $"Customer account {verb}"),
            "admin_users" => ("User account", $"Administrator account {verb}"),
            "account_identities" => ("User account", $"Login identity {verb}"),
            "services" or "makeup_addons" or "catalog_categories" or "catalog_images" => ("Service", $"Catalog ({table.Replace('_', ' ')}) {verb}"),
            "business_settings" or "service_areas" or "payment_methods" => ("Settings", $"Business settings ({table.Replace('_', ' ')}) {verb}"),
            "blocked_slots" => ("Schedule", $"Unavailable time {verb}"),
            "reschedule_requests" => ("Booking", $"Reschedule request {verb}"),
            "notifications" => ("Notification", $"Notification {verb}"),
            "media_assets" => ("Website Content", $"Media file {verb}"),
            "makeup_artists" => ("Settings", $"Artist profile {verb}"),
            _ => ("System", $"{table.Replace('_', ' ')} {verb}")
        };
    }

    private static string CategoryOfMessage(string m) =>
        m.Contains("account", StringComparison.OrdinalIgnoreCase) || m.Contains("administrator", StringComparison.OrdinalIgnoreCase) ? "User account"
        : m.Contains("polic", StringComparison.OrdinalIgnoreCase) || m.Contains("setting", StringComparison.OrdinalIgnoreCase) ? "Settings"
        : "System";

    // Filters newest first, then returns one page of rows, the match count and the clamped page number.
    public static (List<OwnerLogEntry> Rows, int Total, int Page) Apply(IEnumerable<OwnerLogEntry> source, OwnerLogFilter f, int page = 1)
    {
        // Newest exact timestamp first. Entries with the very same time keep the newest-recorded one on top
        // (sources append in the order things happened), so the order never falls back to the date alone.
        var rows = source.Select((e, index) => (e, index)).Where(x => x.e is var e &&
            (f.Category.Length == 0 || e.Category == f.Category) &&
            (f.From is not { } from || DateOnly.FromDateTime(e.At) >= from) &&
            (f.To is not { } to || DateOnly.FromDateTime(e.At) <= to) &&
            (f.Q.Length == 0 || e.Action.Contains(f.Q, StringComparison.OrdinalIgnoreCase) || e.ActorName.Contains(f.Q, StringComparison.OrdinalIgnoreCase)
                || e.Record.Contains(f.Q, StringComparison.OrdinalIgnoreCase) || e.Result.Contains(f.Q, StringComparison.OrdinalIgnoreCase)
                || (e.Archive?.Any(kv => kv.Value.Contains(f.Q, StringComparison.OrdinalIgnoreCase)) ?? false)))
            .OrderByDescending(x => x.e.At).ThenByDescending(x => x.index).Select(x => x.e).ToList();
        var pages = Math.Max(1, (rows.Count + f.PageSize - 1) / f.PageSize);
        page = Math.Clamp(page, 1, pages);
        return (rows.Skip((page - 1) * f.PageSize).Take(f.PageSize).ToList(), rows.Count, page);
    }
}
