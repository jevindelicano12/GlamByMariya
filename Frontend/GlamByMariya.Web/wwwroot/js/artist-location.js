// Lets the owner place the artist's home base and current location on an OpenStreetMap map (Leaflet).
// The draggable pin fills the hidden latitude/longitude of the two save forms; the server checks the
// Metro Manila box again.
(() => {
  const root = document.querySelector('[data-artist-location]');
  if (!root || !window.L) return;

  const bounds = { minLat: 14.20, maxLat: 14.95, minLng: 120.85, maxLng: 121.25 };
  const metroManila = [14.5995, 120.9842];
  const outsideMessage = "Pin the artist's location inside Metro Manila.";
  const geolocationMessage = "We couldn't get your location. Drag the pin instead.";
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
    }, () => { geoButton.disabled = false; say(geolocationMessage); }, { enableHighAccuracy: true, timeout: 10000 });
  });
})();
