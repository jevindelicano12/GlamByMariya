// Shows the customer's pinned service location on a read-only OpenStreetMap map (Leaflet).
(() => {
  if (!window.L) return;
  document.querySelectorAll('[data-booking-location-view]').forEach(el => {
    const lat = parseFloat(el.dataset.lat);
    const lng = parseFloat(el.dataset.lng);
    if (!Number.isFinite(lat) || !Number.isFinite(lng)) return;
    const map = L.map(el, { dragging: false, scrollWheelZoom: false, doubleClickZoom: false, boxZoom: false, keyboard: false, touchZoom: false }).setView([lat, lng], 16);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '<a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">© OpenStreetMap contributors</a>'
    }).addTo(map);
    L.marker([lat, lng], { draggable: false, keyboard: false, title: 'Service location pin' }).addTo(map);
  });
})();
