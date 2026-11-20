# Booking location pin — design

Date: 2026-11-20. Approach A chosen by the owner in chat.

## Intent

Glam by Mariya is home-service: the artist travels to the customer. Typed
addresses ("Sta Lucia ...") are often not enough to find the house. When
booking, the customer pins the exact spot on a map so the artist does not
get lost; the owner/artist opens that spot in Google Maps to navigate.

## Goals

1. Booking step 2 ("Choose your service location") shows a map centred on
   the chosen service area. The customer sets the pin by dragging it,
   tapping the map, "Use my current location" (browser GPS), or searching
   an address/landmark.
2. A pin is required to submit a booking.
3. The pin (latitude, longitude) is stored with the booking.
4. Admin booking details show the pin on a small map and an
   "Open in Google Maps" button (directions to the coordinates).
5. Customer booking details show the pinned location (read-only).

## Non-goals

- Travel time/distance estimates or transport fees from the map.
- Checking the pin is inside the exact city chosen (no city boundary data).
- Changing the pin after booking (a reschedule keeps the same location).
- Google Maps API, keys or billing.

## Anti-goals

- A booking with no usable location.
- The page breaking or the booking being impossible when the map or
  search service is slow: the customer can still drag the pin manually.
- Sending customer data to third parties beyond what map use requires
  (tiles; the search text only when the customer searches).

## Constraints

- Backend is C# only (ASP.NET Core); the map runs in the browser (JS).
- Free: OpenStreetMap tiles with attribution; Leaflet served from the app
  (`wwwroot/lib/leaflet`), not a third-party CDN at runtime.
- Search uses OpenStreetMap Nominatim, Philippines only
  (`countrycodes=ph`), only on an explicit search, at most one request
  per second (Nominatim usage policy).
- Database changes through a new numbered migration.

## Approaches

- **A. OpenStreetMap + Leaflet (chosen):** free, no key; slightly less
  detailed search than Google in some barangays.
- B. Google Maps + Places: best search, needs the owner's billing account
  and API key.
- C. GPS button only, no map: lightest, but the customer can't see or
  correct a wrong location.

## Design

1. **Migration 0013**: `reservations.location_lat numeric(9,6)`,
   `location_lng numeric(9,6)`, both nullable (old bookings have none),
   with a check that both are set or both null and that they fall in a
   Metro Manila bounding box (lat 14.20–14.95, lng 120.85–121.25). New
   app-managed bookings must have both (reservation guard on insert).
   The booking summary view exposes them.
2. **Domain/store (C#)**: `Booking.Latitude/Longitude (decimal?)`;
   `SubmitBooking` gains `decimal latitude, decimal longitude`;
   `BookingService` validates the same box before saving (friendly
   message: "Pin your exact location inside Metro Manila."). Demo store
   keeps them in memory.
3. **Booking form**: hidden `latitude`/`longitude` inputs; map + buttons
   under the address; area change re-centres the map (fixed centre per
   area; default Metro Manila); submit stays disabled until a pin is set;
   the review popup shows "Location pinned ✓".
4. **Admin and customer details**: small read-only map with the pin and
   an "Open in Google Maps" link
   (`https://www.google.com/maps/dir/?api=1&destination=lat,lng`).
   Bookings without a pin show "No pinned location".

## Left to the builder

Map height and styling, marker icon, exact area centre coordinates,
search box layout, and the wording of helper text.
