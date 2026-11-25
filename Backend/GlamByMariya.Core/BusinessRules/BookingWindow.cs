using GlamByMariya.Core.Entities;

namespace GlamByMariya.Core.BusinessRules;

public enum SlotKind { Regular, Early }

// The bookable day (Phase 6): regular opening hours, plus an admin-set early-request start before
// opening and an admin-set latest end after closing. Unset hours mean "same as opening/closing".
public static class BookingWindow
{
    public const string AfterHoursNote = "This booking ends after closing ({0}). That's fine, but any after-hours fee is agreed directly with the artist. It isn't included in this total and isn't collected by the website.";
    public const string EarlyGroupLabel = "Early booking (needs artist approval)";

    public static int EarliestHour(BusinessSettings s) => s.EarlyRequestStartHour ?? s.OpenHour;
    public static int LatestHour(BusinessSettings s) => s.AfterHoursEndHour ?? s.CloseHour;

    public static SlotKind KindOf(DateTime start, BusinessSettings s) =>
        start.TimeOfDay < TimeSpan.FromHours(s.OpenHour) ? SlotKind.Early : SlotKind.Regular;

    // Late / after-hours starts at the regular closing hour (not daytime slots whose duration runs past closing).
    public static bool IsAfterHours(DateTime start, int durationMinutes, BusinessSettings s) =>
        start.TimeOfDay >= TimeSpan.FromHours(s.CloseHour);

    public static bool Fits(DateTime start, int durationMinutes, int bufferMinutes, BusinessSettings s) =>
        start >= start.Date.AddHours(EarliestHour(s))
        && start < start.Date.AddHours(LatestHour(s))
        && start.AddMinutes(durationMinutes + bufferMinutes) <= start.Date.AddHours(24);

    // Null when valid: early <= open < close <= latest <= 24, no crossing midnight.
    public static string? ValidateSettings(int open, int close, int? early, int? latest)
    {
        if (early is { } e && (e < 0 || e > open)) return "Early booking cannot start after opening.";
        if (latest is { } l && (l < close || l > 24)) return "Latest end cannot be before closing.";
        return null;
    }
}
