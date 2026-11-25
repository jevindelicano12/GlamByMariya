using System.Globalization;

namespace GlamByMariya.Core.BusinessRules;

// The customer's exact pin for a home-service booking. It must sit inside the
// service-area box: Metro Manila and Rizal (inclusive); the database enforces the same box.
public static class BookingLocation
{
    public const decimal MinLat = 14.20m, MaxLat = 14.95m, MinLng = 120.85m, MaxLng = 121.65m;
    public const string Message = "Pin your exact location inside Metro Manila or Rizal.";
    public const string ArtistMessage = "Pin the artist's location inside Metro Manila or Rizal.";

    public static bool IsValid(decimal? lat, decimal? lng) =>
        lat is { } y && lng is { } x && y >= MinLat && y <= MaxLat && x >= MinLng && x <= MaxLng;

    // Directions from wherever the artist is to the pin.
    public static string GoogleMapsUrl(decimal lat, decimal lng) =>
        "https://www.google.com/maps/dir/?api=1&destination=" +
        lat.ToString("F6", CultureInfo.InvariantCulture) + "," + lng.ToString("F6", CultureInfo.InvariantCulture);

    private static string NormalizePlace(string value)
    {
        var s = value.Trim().ToLowerInvariant();
        if (s.StartsWith("city of ", StringComparison.Ordinal)) s = s[8..].Trim();
        if (s.StartsWith("municipality of ", StringComparison.Ordinal)) s = s[16..].Trim();
        if (s.EndsWith(" city", StringComparison.Ordinal) && s != "quezon city") s = s[..^5].Trim();
        return s;
    }

    // Rejects structured Philippine addresses whose city/province is outside Mariya's configured service areas.
    public static string? AddressCoverageError(string? address, string? configuredAreas)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(configuredAreas)) return null;
        var clean = address.Split("(Landmark:", StringSplitOptions.None)[0].Trim();
        var segments = clean.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasPhZip = System.Text.RegularExpressions.Regex.IsMatch(clean, @"\b\d{4}\s*$");
        if (segments.Length < 4 && !hasPhZip) return null; // Simple legacy street-only address; area dropdown governs.
        var allowed = configuredAreas.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allowedNorm = allowed.Select(NormalizePlace).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = segments.Any(seg => allowedNorm.Contains(NormalizePlace(seg)));
        return matched ? null : $"This service address is outside Glam by Mariya's service areas ({string.Join(", ", allowed)}). Please use an address within our covered cities.";
    }
}
