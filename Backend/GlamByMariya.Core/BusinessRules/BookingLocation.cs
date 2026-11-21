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
}
