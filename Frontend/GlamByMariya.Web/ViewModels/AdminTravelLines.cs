using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Core.Entities;

namespace GlamByMariya.Web.ViewModels;

// The travel line shown between two consecutive bookings of a day on the owner pages. Computed live
// from the current travel settings, so changing the speed or minimum updates every line at once.
public static class AdminTravelLines
{
    // Keyed by the later booking's id; the text sits just above it.
    public static Dictionary<Guid, string> For(IEnumerable<Booking> dayBookings, BusinessSettings settings)
    {
        var lines = new Dictionary<Guid, string>();
        Booking? before = null;
        foreach (var current in dayBookings.OrderBy(b => b.Start))
        {
            if (before != null) lines[current.Id] = Line(before, current, settings);
            before = current;
        }
        return lines;
    }

    public static string Line(Booking from, Booking to, BusinessSettings settings)
    {
        var minutes = TravelPlanner.Minutes(from.Latitude, from.Longitude, to.Latitude, to.Longitude, settings.TravelSpeedKmh, settings.TravelMinimumMinutes);
        var pinless = from.Latitude is null || from.Longitude is null || to.Latitude is null || to.Longitude is null;
        return pinless ? $"≈ {minutes} min (no pin)" : $"≈ {minutes} min travel";
    }

    private static string Unit(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")} ago";

    public static string Ago(DateTime? updatedAt, DateTime now)
    {
        if (updatedAt is not { } at) return "";
        var span = now - at;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? Unit((int)span.TotalMinutes, "minute") : span.TotalDays < 1 ? Unit((int)span.TotalHours, "hour") : Unit((int)span.TotalDays, "day");
    }
}
