// Service-location pin for new bookings. The customer pins where the artist should go
// before the calendar shows times: available times depend on travel between appointments.
// The server validates the pin again; this script only collects it.
(() => {
  const form = document.querySelector('#booking-form');
  const panel = form?.querySelector('[data-service-map]');
  if (!form || !panel) return;
  const latInput = form.querySelector('input[name="serviceLat"]');
  const lngInput = form.querySelector('input[name="serviceLng"]');
  const status = panel.querySelector('[data-map-status]');
  const locate = panel.querySelector('[data-map-locate]');
  const mapElement = panel.querySelector('[data-map]');
  const center = [Number(panel.dataset.centerLat), Number(panel.dataset.centerLng)];
  const radiusKm = Number(panel.dataset.radiusKm);

  const distanceKm = (a, b) => {
    const rad = d => d * Math.PI / 180;
    const h = Math.sin(rad(b[0] - a[0]) / 2) ** 2 + Math.cos(rad(a[0])) * Math.cos(rad(b[0])) * Math.sin(rad(b[1] - a[1]) / 2) ** 2;
    return 2 * 6371.0088 * Math.asin(Math.min(1, Math.sqrt(h)));
  };
  const say = (text, state = '') => { status.textContent = text; status.dataset.state = state; };
  const announce = () => form.dispatchEvent(new CustomEvent('service-pin-changed', { bubbles: true }));

  let map = null;
  let marker = null;
  const clearPin = () => {
    latInput.value = ''; lngInput.value = '';
    form.dataset.pinned = 'false';
    announce();
  };
  const setPin = (lat, lng, { move = false } = {}) => {
    if (!Number.isFinite(lat) || !Number.isFinite(lng) || Math.abs(lat) > 90 || Math.abs(lng) > 180) {
      say('That location could not be read. Please tap the map again.', 'error');
      return;
    }
    if (distanceKm(center, [lat, lng]) > radiusKm) {
      if (marker) { marker.remove(); marker = null; }
      clearPin();
      say(`That spot is outside our service area (within ${radiusKm} km). Please pin a location inside the circle.`, 'error');
      return;
    }
    latInput.value = lat.toFixed(6);
    lngInput.value = lng.toFixed(6);
    form.dataset.pinned = 'true';
    if (map) {
      if (!marker) {
        marker = window.L.marker([lat, lng], { draggable: true, keyboard: true, title: 'Your service location', alt: 'Your service location' }).addTo(map);
        marker.on('dragend', () => { const p = marker.getLatLng(); setPin(p.lat, p.lng); });
      } else marker.setLatLng([lat, lng]);
      if (move) map.setView([lat, lng], Math.max(map.getZoom(), 16));
    }
    say('Location pinned. Drag the pin to adjust. The times below include travel from the artist\'s other appointments.', 'ok');
    announce();
  };

  if (window.L && mapElement) {
    try {
      map = window.L.map(mapElement, { scrollWheelZoom: false, tap: true }).setView(center, 12);
      window.L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a> contributors'
      }).addTo(map);
      window.L.circle(center, { radius: radiusKm * 1000, color: '#A6536B', weight: 1, fillOpacity: 0.04, interactive: false }).addTo(map);
      map.on('click', event => setPin(event.latlng.lat, event.latlng.lng));
      // The map can be created while its step is hidden; size it once it is shown.
      new ResizeObserver(() => map.invalidateSize()).observe(mapElement);
    } catch {
      map = null;
    }
  }
  if (!map) {
    mapElement?.setAttribute('hidden', '');
    say('The map could not load. Use "Use my current location" to pin where you are, or refresh the page.', 'error');
  }

  locate?.addEventListener('click', () => {
    if (!navigator.geolocation) { say('Your browser cannot share your location. Please tap your location on the map.', 'error'); return; }
    locate.disabled = true;
    say('Finding your location…');
    navigator.geolocation.getCurrentPosition(
      position => { locate.disabled = false; setPin(position.coords.latitude, position.coords.longitude, { move: true }); },
      error => {
        locate.disabled = false;
        say(error.code === error.PERMISSION_DENIED
          ? 'Location access was blocked. Please tap your location on the map instead.'
          : 'Your location could not be found. Please tap your location on the map instead.', 'error');
      },
      { enableHighAccuracy: true, timeout: 15000, maximumAge: 60000 });
  });

  // Returning to this step (or a browser restoring the form) keeps an existing pin.
  if (latInput.value && lngInput.value) setPin(Number(latInput.value), Number(lngInput.value), { move: true });
  else { form.dataset.pinned = 'false'; if (map) say('Tap the map where the artist should go, or use your current location.'); }
})();
