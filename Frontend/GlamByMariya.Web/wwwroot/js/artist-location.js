// Lets the owner place the artist's home base and current location on an OpenStreetMap map (Leaflet).
// The draggable pin fills the hidden latitude/longitude of the two save forms; the server checks the
// Metro Manila and Rizal box again.
(() => {
  const root = document.querySelector('[data-artist-location]');
  if (!root || !window.L) return;

  const bounds = { minLat: 14.20, maxLat: 14.95, minLng: 120.85, maxLng: 121.65 };
  const metroManila = [14.5995, 120.9842];
  const outsideMessage = "Pin the artist's location inside Metro Manila or Rizal.";
  const geolocationMessage = "We couldn't get your location. Drag the pin instead.";
  // Say why the browser gave no location: blocked for this site, device location off, or too slow.
  const geolocationError = error => error?.code === 1
    ? "Location is blocked for this site in your browser. Allow location in the browser's site settings and try again, or drag the pin."
    : error?.code === 2
      ? "Your device couldn't find its location. Turn on location services on your device and try again, or drag the pin."
      : error?.code === 3 ? "Finding your location took too long. Try again, or drag the pin." : geolocationMessage;
  const inside = (lat, lng) => lat >= bounds.minLat && lat <= bounds.maxLat && lng >= bounds.minLng && lng <= bounds.maxLng;
  const read = (a, b) => { const lat = parseFloat(root.dataset[a]); const lng = parseFloat(root.dataset[b]); return Number.isFinite(lat) && Number.isFinite(lng) ? [lat, lng] : null; };
  const home = read('homeLat', 'homeLng');
  const current = read('currentLat', 'currentLng');

  const status = root.querySelector('[data-artist-location-status]');
  const forms = [...root.querySelectorAll('[data-artist-location-form]')];
  const geoButton = root.querySelector('[data-artist-location-geolocate]');
  const say = text => { status.textContent = text; };

  const start = current || home || metroManila;
  const map = L.map(root.querySelector('[data-artist-location-map]'), { scrollWheelZoom: false }).setView(start, current || home ? 15 : 12);
  L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '<a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">© OpenStreetMap contributors</a>'
  }).addTo(map);

  // Saved pins are drawn as circles so they are visible next to the draggable working pin.
  if (home) L.circleMarker(home, { radius: 9, color: '#305545', fillColor: '#305545', fillOpacity: .85 }).bindTooltip('Home base', { permanent: true, direction: 'top' }).addTo(map);
  if (current) L.circleMarker(current, { radius: 9, color: '#b8863b', fillColor: '#b8863b', fillOpacity: .85 }).bindTooltip('Current location', { permanent: true, direction: 'bottom' }).addTo(map);

  const marker = L.marker(start, { draggable: true, keyboard: true, title: 'Pin to save' }).addTo(map);

  const hasSaved = !!(current || home);
  const setPin = (lat, lng, message, placed = true) => {
    marker.setLatLng([lat, lng]);
    const ok = inside(lat, lng);
    forms.forEach(form => {
      form.querySelector('[name="latitude"]').value = ok ? lat.toFixed(6) : '';
      form.querySelector('[name="longitude"]').value = ok ? lng.toFixed(6) : '';
      form.querySelector('button').disabled = !ok || !placed;
    });
    say(!placed ? message : ok ? (message || 'Pin placed. Save it as the current location or as home base.') : outsideMessage);
  };

  marker.on('dragend', () => { const p = marker.getLatLng(); setPin(p.lat, p.lng); });
  map.on('click', event => setPin(event.latlng.lat, event.latlng.lng));
  setPin(start[0], start[1], current || home ? 'Drag the pin to move it, then save.' : 'Drag the pin or click the map to where the artist is based, then save.', hasSaved);

  geoButton?.addEventListener('click', () => {
    if (!navigator.geolocation) { say(geolocationMessage); return; }
    geoButton.disabled = true;
    say('Finding your location…');
    navigator.geolocation.getCurrentPosition(position => {
      geoButton.disabled = false;
      const { latitude, longitude } = position.coords;
      map.setView([latitude, longitude], 16);
      setPin(latitude, longitude);
    }, error => { geoButton.disabled = false; say(geolocationError(error)); }, { enableHighAccuracy: true, timeout: 15000, maximumAge: 60000 });
  });

  // Search a place or address (works even where the browser blocks location). Same Nominatim
  // rules as the booking map: an explicit search only, the Philippines only, at most one per second.
  const query = root.querySelector('[data-artist-location-query]');
  const searchButton = root.querySelector('[data-artist-location-search]');
  let lastSearch = 0;
  let searching = false;
  const search = async () => {
    const text = query.value.trim();
    if (!text || searching) return;
    if (lastSearch + 1000 > Date.now()) { say('Please wait a moment before searching again.'); return; }
    lastSearch = Date.now();
    searching = true;
    searchButton.disabled = true;
    say('Searching…');
    try {
      const response = await fetch(`https://nominatim.openstreetmap.org/search?format=json&limit=1&countrycodes=ph&viewbox=120.85,14.95,121.65,14.20&bounded=1&q=${encodeURIComponent(text)}`, {
        headers: { Accept: 'application/json' }, credentials: 'omit', referrerPolicy: 'strict-origin-when-cross-origin'
      });
      if (!response.ok) throw new Error('search failed');
      const results = await response.json();
      const hit = Array.isArray(results) ? results[0] : null;
      const lat = hit ? parseFloat(hit.lat) : NaN, lng = hit ? parseFloat(hit.lon) : NaN;
      if (!Number.isFinite(lat) || !Number.isFinite(lng)) { say('No match found. Try another name, or drag the pin.'); return; }
      map.setView([lat, lng], 16);
      setPin(lat, lng, 'Pinned near your search result. Drag the pin to the exact spot, then save.');
    } catch {
      say('Search is busy right now. Try again in a moment, or drag the pin.');
    } finally {
      searching = false;
      searchButton.disabled = false;
    }
  };
  searchButton?.addEventListener('click', search);
  query?.addEventListener('keydown', event => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    search();
  });
})();
