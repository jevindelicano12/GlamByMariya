// Lets the customer pin the exact service location on an OpenStreetMap map (Leaflet).
// The pin is written to the hidden latitude/longitude inputs with six decimals; the
// server checks it again against the Metro Manila and Rizal box.
(() => {
  const root = document.querySelector('#booking-location');
  const form = document.querySelector('#booking-form');
  if (!root || !form || !window.L) return;

  const centres = {
    'Quezon City': [14.6760, 121.0437],
    'Makati': [14.5547, 121.0244],
    'Manila': [14.5995, 120.9842],
    'Pasig': [14.5764, 121.0851],
    'Taguig': [14.5176, 121.0509],
    'Mandaluyong': [14.5794, 121.0359],
    'Marikina': [14.6507, 121.1029],
    'Rizal': [14.6255, 121.1245]
  };
  const metroManila = [14.5995, 120.9842];
  const bounds = { minLat: 14.20, maxLat: 14.95, minLng: 120.85, maxLng: 121.65 };
  const outsideMessage = 'Pin your exact location inside Metro Manila or Rizal.';
  const geolocationMessage = "We couldn't get your location. Drag the pin instead.";
  // Say why the browser gave no location: blocked for this site, device location off, or too slow.
  const geolocationError = error => error?.code === 1
    ? "Location is blocked for this site in your browser. Allow location in the browser's site settings and try again, or drag the pin."
    : error?.code === 2
      ? "Your device couldn't find its location. Turn on location services on your device and try again, or drag the pin."
      : error?.code === 3 ? "Finding your location took too long. Try again, or drag the pin." : geolocationMessage;
  const noMatchMessage = 'No match found in Metro Manila or Rizal; drag the pin instead';
  const busyMessage = 'Search is busy right now. Try again in a moment, or drag the pin.';

  const latitude = root.querySelector('[name="latitude"]');
  const longitude = root.querySelector('[name="longitude"]');
  const status = root.querySelector('#booking-location-status');
  const query = root.querySelector('#booking-location-query');
  const searchButton = root.querySelector('[data-location-search]');
  const currentButton = root.querySelector('[data-location-current]');
  const area = form.querySelector('select[name="area"]');

  const say = text => { status.textContent = text; };
  const inside = (lat, lng) => lat >= bounds.minLat && lat <= bounds.maxLat && lng >= bounds.minLng && lng <= bounds.maxLng;
  const hasPin = () => latitude.value !== '' && longitude.value !== '';
  const areaCentre = () => centres[area?.value?.trim()] || metroManila;

  const announce = () => form.dispatchEvent(new CustomEvent('booking-location-changed', { bubbles: true }));
  const saved = [parseFloat(latitude.value), parseFloat(longitude.value)];
  const restored = Number.isFinite(saved[0]) && Number.isFinite(saved[1]) && inside(saved[0], saved[1]);
  if (!restored) { latitude.value = ''; longitude.value = ''; announce(); }
  const startAt = restored ? saved : areaCentre();

  const map = L.map(root.querySelector('#booking-location-map'), { scrollWheelZoom: false }).setView(startAt, restored ? 17 : 14);
  L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '<a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">© OpenStreetMap contributors</a>'
  }).addTo(map);
  const marker = L.marker(startAt, { draggable: true, keyboard: true, title: 'Service location pin' }).addTo(map);
  if (restored) say('Pinned. Drag the pin if you need to adjust it.');

  const setPin = (lat, lng, message) => {
    marker.setLatLng([lat, lng]);
    if (!inside(lat, lng)) {
      latitude.value = ''; longitude.value = '';
      say(outsideMessage);
    } else {
      latitude.value = lat.toFixed(6); longitude.value = lng.toFixed(6);
      say(message || 'Pinned. Drag the pin if you need to adjust it.');
    }
    latitude.dispatchEvent(new Event('change', { bubbles: true }));
    announce();
  };

  marker.on('dragend', () => { const p = marker.getLatLng(); setPin(p.lat, p.lng); });
  map.on('click', event => setPin(event.latlng.lat, event.latlng.lng));

  // A changed area re-centres the map only while no pin is set.
  area?.addEventListener('change', () => {
    if (hasPin()) return;
    const centre = areaCentre();
    map.setView(centre, 14);
    marker.setLatLng(centre);
  });

  const locate = (onSuccess, onError) => {
    let settled = false;
    let best = null;
    let watchId = null;
    let settleTimer = null;
    const cleanup = () => {
      if (watchId !== null) { navigator.geolocation.clearWatch(watchId); watchId = null; }
      if (settleTimer !== null) { clearTimeout(settleTimer); settleTimer = null; }
    };
    const finish = pos => {
      if (settled) return;
      settled = true;
      cleanup();
      onSuccess(pos);
    };
    const fail = error => {
      if (settled) return;
      settled = true;
      cleanup();
      onError(error);
    };
    settleTimer = setTimeout(() => {
      if (best) { finish(best); return; }
      cleanup();
      navigator.geolocation.getCurrentPosition(finish, fail, { enableHighAccuracy: false, timeout: 10000, maximumAge: 0 });
    }, 6000);
    watchId = navigator.geolocation.watchPosition(pos => {
      const acc = pos.coords?.accuracy;
      if (!best || (Number.isFinite(acc) && (!Number.isFinite(best.coords?.accuracy) || acc < best.coords.accuracy))) best = pos;
      if (Number.isFinite(acc) && acc <= 50) finish(pos);
    }, error => {
      if (error?.code === 1) { fail(error); return; }
      if (best) { finish(best); return; }
      cleanup();
      navigator.geolocation.getCurrentPosition(finish, fail, { enableHighAccuracy: false, timeout: 10000, maximumAge: 0 });
    }, { enableHighAccuracy: true, timeout: 10000, maximumAge: 0 });
  };

  currentButton?.addEventListener('click', () => {
    if (!navigator.geolocation) { say(geolocationMessage); return; }
    currentButton.disabled = true;
    say('Finding your location…');
    locate(position => {
      currentButton.disabled = false;
      const { latitude: lat, longitude: lng, accuracy } = position.coords;
      map.invalidateSize();
      map.setView([lat, lng], accuracy > 500 ? 15 : 17);
      const message = Number.isFinite(accuracy) && accuracy > 500
        ? `Approximate location found (±${(accuracy / 1000).toFixed(1)} km). Drag the pin to your exact spot.`
        : 'Pinned at your current location. Drag the pin if you need to adjust it.';
      setPin(lat, lng, message);
    }, error => {
      currentButton.disabled = false;
      say(geolocationError(error));
    });
  });

  // Nominatim is used only on an explicit search, at most one request per second.
  let lastSearch = 0;
  let searching = false;
  const search = async () => {
    const text = query.value.trim();
    if (!text || searching) return;
    const wait = lastSearch + 1000 - Date.now();
    if (wait > 0) { say('Please wait a moment before searching again.'); return; }
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
      if (!Number.isFinite(lat) || !Number.isFinite(lng)) { say(noMatchMessage); return; }
      map.setView([lat, lng], 17);
      setPin(lat, lng, 'Pinned near your search result. Drag the pin to your exact spot.');
    } catch {
      say(busyMessage);
    } finally {
      searching = false;
      searchButton.disabled = false;
    }
  };
  searchButton?.addEventListener('click', search);
  // Enter in the search box searches instead of moving to the next booking step.
  query?.addEventListener('keydown', event => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    event.stopPropagation();
    search();
  });

  // The map starts inside a hidden booking step; redraw it once it becomes visible.
  if (window.ResizeObserver) new ResizeObserver(() => map.invalidateSize()).observe(root.querySelector('#booking-location-map'));
  form.addEventListener('booking-step-shown', () => map.invalidateSize());
})();
