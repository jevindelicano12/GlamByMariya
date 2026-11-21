using GlamByMariya.Core.Entities;

namespace GlamByMariya.Core.BusinessRules;

// Built-in travel estimate between two pinned service locations. Mirrors
// public.mvc_travel_minutes in database/migrations/0013_travel_time.sql; in database
// mode the database computes the minutes and remains the final authority.
public static class TravelEstimate
{
    public const double SameSpotKm = 0.1;

    public static double DistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(lat2 - lat1) / 2), 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(Rad(lng2 - lng1) / 2), 2);
        return 2 * 6371.0088 * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    // Minutes the artist needs between two bookings. Neither pinned (bookings made before
    // pins existed): 0, the old rule. Only one pinned: the configured fallback.
    public static int Minutes(BusinessSettings settings, double? lat1, double? lng1, double? lat2, double? lng2)
    {
        var firstKnown = lat1.HasValue && lng1.HasValue;
        var secondKnown = lat2.HasValue && lng2.HasValue;
        if (!firstKnown && !secondKnown) return 0;
        if (!firstKnown || !secondKnown) return settings.TravelUnknownMinutes;
        var km = DistanceKm(lat1!.Value, lng1!.Value, lat2!.Value, lng2!.Value);
        if (km < SameSpotKm) return 0;
        var raw = km * (double)settings.TravelRoadFactor / (double)settings.TravelSpeedKmh * 60 + settings.TravelMarginMinutes;
        return (int)(Math.Ceiling(Math.Ceiling(raw) / 5.0) * 5);
    }

    // A customer's pin must be a real coordinate inside the service radius.
    public static string? ValidatePin(BusinessSettings settings, double? lat, double? lng)
    {
        if (!lat.HasValue || !lng.HasValue) return "Pin your service location on the map before choosing a time.";
        var (y, x) = (lat.Value, lng.Value);
        if (double.IsNaN(y) || double.IsNaN(x) || double.IsInfinity(y) || double.IsInfinity(x) || y is < -90 or > 90 || x is < -180 or > 180)
            return "The pinned location is not valid. Please pin your location again.";
        if (DistanceKm((double)settings.ServiceCenterLat, (double)settings.ServiceCenterLng, y, x) > (double)settings.ServiceRadiusKm)
            return "The pinned location is outside our service area. Please pin a location within our service area.";
        return null;
    }

    // Six decimal places (about 11 cm), the precision stored in the database.
    public static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
