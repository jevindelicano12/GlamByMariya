namespace GlamByMariya.Core.BusinessRules;

// One appointment the artist already holds on a day (Asia/Manila local times).
public sealed record TravelStop(DateTime Start, DateTime End, int BufferMinutes, decimal? Latitude, decimal? Longitude);

// The travel rule: can the artist get from the stop before to a new pin, and on to the stop after?
// Pure and deterministic; no I/O.
public static class TravelPlanner
{
    public const decimal RoadFactor = 1.3m;
    public const string RefusalMessage = "That time no longer fits the artist's travel. Choose another time.";
    private const double EarthRadiusKm = 6371.0;

    // Straight-line distance times the road factor at the given speed, rounded up to 15 minutes and
    // never below the minimum. Any missing pin takes the minimum.
    public static int Minutes(decimal? fromLat, decimal? fromLng, decimal? toLat, decimal? toLng, decimal speedKmh, int minimumMinutes)
    {
        if (fromLat is not { } la1 || fromLng is not { } lo1 || toLat is not { } la2 || toLng is not { } lo2 || speedKmh <= 0)
            return minimumMinutes;
        double rad(decimal d) => (double)d * Math.PI / 180.0;
        double dLat = rad(la2) - rad(la1), dLng = rad(lo2) - rad(lo1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(rad(la1)) * Math.Cos(rad(la2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        double km = 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        double minutes = km * (double)RoadFactor / (double)speedKmh * 60.0;
        int rounded = (int)(Math.Ceiling(minutes / 15.0 - 1e-9) * 15);
        return Math.Max(minimumMinutes, rounded);
    }

    // Overlaps are not this rule's job: the existing overlap check rejects them.
    public static bool Fits(DateTime start, int durationMinutes, int bufferMinutes, decimal lat, decimal lng,
        IReadOnlyList<TravelStop> sameDayStops, (decimal? Lat, decimal? Lng)? artistNow, DateTime now,
        decimal speedKmh, int minimumMinutes)
    {
        var end = start.AddMinutes(durationMinutes);
        var previous = sameDayStops.Where(s => s.End <= start).OrderByDescending(s => s.End).FirstOrDefault();
        var next = sameDayStops.Where(s => s.Start >= end).OrderBy(s => s.Start).FirstOrDefault();

        if (previous is not null)
        {
            var travel = Minutes(previous.Latitude, previous.Longitude, lat, lng, speedKmh, minimumMinutes);
            if (previous.End.AddMinutes(previous.BufferMinutes + travel) > start) return false;
        }
        else if (start.Date == now.Date && artistNow is { } here)
        {
            if (now.AddMinutes(Minutes(here.Lat, here.Lng, lat, lng, speedKmh, minimumMinutes)) > start) return false;
        }

        if (next is not null)
        {
            var travel = Minutes(lat, lng, next.Latitude, next.Longitude, speedKmh, minimumMinutes);
            if (end.AddMinutes(bufferMinutes + travel) > next.Start) return false;
        }
        return true;
    }
}
