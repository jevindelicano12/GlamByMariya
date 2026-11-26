// Standard Philippine address picker: Region → Province → City/Municipality → Barangay,
// plus house/street and ZIP. Enhances any textarea[data-ph-address]; the textarea stays the
// submitted "address" field and receives the composed address, so the server is unchanged.
(() => {
  const fields = document.querySelectorAll('textarea[data-ph-address]');
  if (!fields.length) return;
  const base = '/data/ph-address/';
  let indexPromise;
  const barangayCache = {};
  const getIndex = () => indexPromise ??= fetch(base + 'index.json').then(r => { if (!r.ok) throw new Error('index'); return r.json(); });
  const getBarangays = prov => barangayCache[prov] ??= fetch(`${base}barangays/${prov}.json`).then(r => { if (!r.ok) throw new Error('barangays'); return r.json(); });

  let uid = 0;
  const el = (tag, props = {}, children = []) => {
    const node = Object.assign(document.createElement(tag), props);
    for (const child of [].concat(children)) node.append(child);
    return node;
  };
  const option = (value, text) => el('option', { value, textContent: text });
  const fill = (select, placeholder, items) => {
    select.replaceChildren(option('', placeholder), ...items.map(([value, text]) => option(value, text)));
    select.disabled = !items.length;
  };

  fields.forEach(textarea => {
    const id = `ph-address-${++uid}`;
    const sourceLabel = textarea.closest('label');
    const savedValue = textarea.value.trim();
    const withLandmark = textarea.hasAttribute('data-ph-landmark');

    const field = (labelText, control, extra = '') => el('label', { className: extra }, [labelText, control]);
    const region = el('select', { id: `${id}-region`, autocomplete: 'off' });
    const province = el('select', { id: `${id}-province`, autocomplete: 'address-level1' });
    const city = el('select', { id: `${id}-city`, autocomplete: 'address-level2' });
    const barangay = el('select', { id: `${id}-barangay`, autocomplete: 'address-level3' });
    const houseNo = el('input', { id: `${id}-house`, autocomplete: 'address-line1', maxLength: 50, placeholder: 'e.g. Unit 4B, Blk 2 Lot 5' });
    const streetName = el('input', { id: `${id}-street`, autocomplete: 'address-line2', maxLength: 80, placeholder: 'e.g. 123 Rizal St.' });
    const subdivision = el('input', { id: `${id}-subdivision`, autocomplete: 'address-line3', maxLength: 80, placeholder: 'e.g. Villa Verde Subd.' });
    const zip = el('input', { id: `${id}-zip`, autocomplete: 'postal-code', inputMode: 'numeric', maxLength: 4, pattern: '[0-9]{4}', placeholder: 'e.g. 1600', title: 'Enter your 4-digit ZIP code' });
    const landmark = el('input', { id: `${id}-landmark`, maxLength: 120, placeholder: 'e.g. Beside the blue gate, near 7-Eleven' });
    const preview = el('p', { className: 'ph-address-preview', role: 'status' });
    preview.setAttribute('aria-live', 'polite');

    const controls = [region, province, city, barangay, houseNo, streetName, zip];
    fill(region, 'Loading regions…', []); fill(province, 'Select province', []); fill(city, 'Select city / municipality', []); fill(barangay, 'Select barangay', []);

    const picker = el('div', { className: 'ph-address-fields form-grid' }, [
      field('Region', region), field('Province', province),
      field('City / Municipality', city), field('Barangay', barangay),
      field('House / Unit / Bldg No.', houseNo), field('Street Name', streetName),
      field('Subdivision / Village (optional)', subdivision), field('ZIP code', zip),
      ...(withLandmark ? [field('Landmark (optional)', landmark, 'full')] : []),
      preview
    ]);
    const wrapper = el('fieldset', { className: 'ph-address full' }, [el('legend', { textContent: sourceLabel?.firstChild?.textContent?.trim() || 'Address' })]);
    const form = textarea.closest('form');
    const areaSelect = form?.querySelector('select[name="area"]');
    const areaError = el('div', { className: 'alert error ph-area-error', hidden: true });
    areaError.setAttribute('role', 'alert');

    const normalizePlace = v => {
      let s = (v || '').trim().toLowerCase();
      if (s.startsWith('city of ')) s = s.slice(8).trim();
      if (s.startsWith('municipality of ')) s = s.slice(16).trim();
      if (s.endsWith(' city') && s !== 'quezon city') s = s.slice(0, -5).trim();
      return s;
    };
    const areaOptions = () => areaSelect ? [...areaSelect.options].map(o => ({ value: o.value || o.textContent.trim(), norm: normalizePlace(o.value || o.textContent) })).filter(o => o.value) : [];
    const matchArea = candidates => {
      const opts = areaOptions();
      for (const c of candidates) {
        const n = normalizePlace(c);
        if (!n) continue;
        const hit = opts.find(o => o.norm === n);
        if (hit) return hit.value;
      }
      return '';
    };
    const checkSavedCoverage = addr => {
      const clean = addr.split('(Landmark:')[0].trim();
      const segs = clean.split(',').map(s => s.trim()).filter(Boolean);
      const hasPhZip = /\b\d{4}\s*$/.test(clean);
      const matched = matchArea(segs);
      if (matched) return { structured: true, covered: true, matched, place: matched };
      if (segs.length >= 4 || hasPhZip) {
        const place = segs.length >= 3 ? segs.slice(-3, -1).join(', ') : clean;
        return { structured: true, covered: false, matched: '', place };
      }
      return { structured: false, covered: true, matched: areaSelect?.value || '', place: '' };
    };

    // Booking: start from saved customer addresses (if any) and let the customer pick, change, or add a new address.
    let savedList = [];
    try {
      const raw = textarea.dataset.savedAddresses;
      if (raw) savedList = JSON.parse(raw).filter(x => x && x.address);
    } catch { }
    if (!savedList.length && savedValue) savedList = [{ label: 'Saved address', address: savedValue, area: '' }];
    let currentSaved = savedList[0]?.address || '';

    let saved;
    if (savedList.length) {
      const change = el('button', { type: 'button', className: 'button secondary', textContent: savedList.length > 1 ? 'Add new address' : 'Change address' });
      const keep = el('button', { type: 'button', className: 'text-button', textContent: 'Use my saved address' });
      const savedStrong = el('strong', { textContent: currentSaved });
      const savedInfo = el('div', {}, [el('span', { textContent: savedList.length > 1 ? 'Saved addresses' : 'Saved address' }), savedStrong]);
      if (savedList.length > 1) {
        const pickerSelect = el('select', { className: 'ph-address-select', ariaLabel: 'Choose a saved address' });
        savedList.forEach((item, idx) => pickerSelect.append(option(String(idx), `${item.label}: ${item.address}`)));
        pickerSelect.addEventListener('change', () => {
          const picked = savedList[Number(pickerSelect.value)] || savedList[0];
          currentSaved = picked.address;
          savedStrong.textContent = currentSaved;
          sync();
        });
        savedInfo.append(pickerSelect);
      }
      saved = el('div', { className: 'ph-address-saved' }, [savedInfo, change]);
      picker.append(el('div', { className: 'full ph-address-keep' }, [keep]));
      change.addEventListener('click', () => setMode(true, true));
      keep.addEventListener('click', () => setMode(false, true));
      wrapper.append(saved);
    } else {
      picker.prepend(el('p', { className: 'full ph-address-empty-note', textContent: 'Add your address here — it will be saved to your profile for future bookings.' }));
    }
    wrapper.append(picker, areaError);
    const errorSlot = sourceLabel?.querySelector('[data-error-for="address"]');
    if (errorSlot) wrapper.append(errorSlot);
    (sourceLabel ?? textarea).after(wrapper);
    if (sourceLabel) sourceLabel.hidden = true; else textarea.hidden = true;
    textarea.required = false;

    let data;
    const regionShort = code => data.regions.find(r => r[0] === code)?.[2] ?? '';
    const compose = () => {
      const p = data && data.provinces.find(x => x[0] === province.value);
      const c = data && data.cities.find(x => x[0] === city.value);
      const parts = [houseNo.value.trim(), streetName.value.trim(), subdivision.value.trim(), barangay.value, c?.[1], p && p[1] !== c?.[1] ? p[1] : '', `${regionShort(region.value)} ${zip.value.trim()}`.trim()];
      let text = parts.filter(Boolean).join(', ');
      if (withLandmark && landmark.value.trim()) text += ` (Landmark: ${landmark.value.trim()})`;
      return text;
    };
    const applyCoverage = (covered, matchedArea, place, fullAddress, geocodeQuery) => {
      if (!areaSelect) return;
      const list = areaOptions().map(o => o.value).join(', ');
      if (!covered) {
        const msg = `Sorry, ${place || 'this city'} is outside Glam by Mariya's service areas (${list}). Bookings outside these cities cannot be accepted — please change your service address to a covered city.`;
        areaError.textContent = msg;
        areaError.hidden = false;
        textarea.setCustomValidity(msg);
        if (!picker.hidden) city.setCustomValidity(msg);
        form.dataset.addressOutOfRange = 'true';
        const outDetail = { address: fullAddress, query: '', area: '', covered: false };
        form._phAddressState = outDetail;
        form.dispatchEvent(new CustomEvent('ph-address-ready', { bubbles: true, detail: outDetail }));
        return;
      }
      areaError.hidden = true;
      areaError.textContent = '';
      textarea.setCustomValidity('');
      city.setCustomValidity('');
      delete form.dataset.addressOutOfRange;
      if (matchedArea && areaSelect.value !== matchedArea) {
        areaSelect.value = matchedArea;
        areaSelect.dispatchEvent(new Event('change', { bubbles: true }));
      }
      if (fullAddress) {
        const okDetail = { address: fullAddress, query: geocodeQuery || fullAddress, area: matchedArea || areaSelect.value, covered: true };
        form._phAddressState = okDetail;
        form.dispatchEvent(new CustomEvent('ph-address-ready', { bubbles: true, detail: okDetail }));
      }
    };
    const sync = () => {
      if (picker.hidden) {
        textarea.value = currentSaved;
        const cov = checkSavedCoverage(currentSaved);
        applyCoverage(cov.covered, cov.matched, cov.place, currentSaved, currentSaved.split('(Landmark:')[0].trim());
        return;
      }
      const p = data?.provinces.find(x => x[0] === province.value);
      const c = data?.cities.find(x => x[0] === city.value);
      if (areaSelect && c) {
        const matched = matchArea([c[1], p?.[1]]);
        const complete = controls.every(control => control.value.trim()) && zip.validity.valid;
        const full = complete ? compose() : '';
        const geoQ = [barangay.value, c[1], p && p[1] !== c[1] ? p[1] : '', 'Philippines'].filter(Boolean).join(', ');
        if (!matched) {
          textarea.value = '';
          preview.textContent = 'Complete every required field to see the full address.';
          applyCoverage(false, '', [c[1], p && p[1] !== c[1] ? p[1] : ''].filter(Boolean).join(', '), '', '');
          textarea.dispatchEvent(new Event('input', { bubbles: true }));
          return;
        }
        textarea.value = full;
        preview.textContent = complete ? `Full address: ${textarea.value}` : 'Complete every required field to see the full address.';
        applyCoverage(true, matched, c[1], full || geoQ, geoQ);
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
        return;
      }
      if (areaSelect && !c) {
        areaError.hidden = true;
        areaError.textContent = '';
        textarea.setCustomValidity('');
        city.setCustomValidity('');
        delete form.dataset.addressOutOfRange;
      }
      const complete = controls.every(control => control.value.trim()) && zip.validity.valid;
      textarea.value = complete ? compose() : '';
      preview.textContent = complete ? `Full address: ${textarea.value}` : 'Complete every required field to see the full address.';
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    };
    function setMode(editing, focus) {
      picker.hidden = !editing;
      if (saved) saved.hidden = editing;
      controls.forEach(control => { control.required = editing; });
      [...controls, subdivision, landmark].forEach(control => { if (!editing) control.disabled = true; });
      if (editing) {
        region.disabled = !data; houseNo.disabled = streetName.disabled = subdivision.disabled = zip.disabled = landmark.disabled = false;
        province.disabled = province.options.length < 2;
        city.disabled = city.options.length < 2; barangay.disabled = barangay.options.length < 2;
        if (focus) region.focus();
      } else if (focus) saved?.querySelector('button')?.focus();
      sync();
    }

    const setProvinces = () => {
      fill(province, 'Select province', data.provinces.filter(p => p[2] === region.value).map(p => [p[0], p[1]]));
      fill(city, 'Select city / municipality', []); fill(barangay, 'Select barangay', []);
      if (province.options.length === 2) { province.selectedIndex = 1; setCities(); }
    };
    const setCities = () => {
      fill(city, 'Select city / municipality', data.cities.filter(c => c[2] === province.value).map(c => [c[0], c[1]]));
      fill(barangay, 'Select barangay', []);
      if (city.options.length === 2) { city.selectedIndex = 1; return setBarangays(); }
      return Promise.resolve();
    };
    const setBarangays = () => {
      if (!city.value) { fill(barangay, 'Select barangay', []); return Promise.resolve(); }
      fill(barangay, 'Loading barangays…', []);
      const cityCode = city.value;
      return getBarangays(province.value).then(map => {
        if (city.value !== cityCode) return;
        fill(barangay, 'Select barangay', (map[cityCode] ?? []).map(name => [name, name]));
      }).catch(fallback);
    };

    region.addEventListener('change', () => { setProvinces(); sync(); });
    province.addEventListener('change', () => { setCities(); sync(); });
    city.addEventListener('change', () => { setBarangays().then(sync); sync(); });
    [barangay, houseNo, streetName, subdivision, zip, landmark].forEach(control => control.addEventListener(control.tagName === 'SELECT' ? 'change' : 'input', sync));
    zip.addEventListener('input', () => { zip.value = zip.value.replace(/\D/g, '').slice(0, 4); });

    getIndex().then(index => {
      const provinceOfAll = Object.fromEntries(index.provinces.map(p => [p[0], p]));
      const provinceNorms = new Set(index.provinces.map(p => normalizePlace(p[1])));
      const coveredCities = areaSelect ? index.cities.filter(c => {
        const provName = provinceOfAll[c[2]]?.[1] || '';
        const cityCandidate = provinceNorms.has(normalizePlace(c[1])) && normalizePlace(provName) !== normalizePlace(c[1]) ? '' : c[1];
        return matchArea([cityCandidate, provName]);
      }) : [];
      if (coveredCities.length) {
        const provIds = new Set(coveredCities.map(c => c[2]));
        const coveredProvinces = index.provinces.filter(p => provIds.has(p[0]));
        const regIds = new Set(coveredProvinces.map(p => p[2]));
        data = { regions: index.regions.filter(r => regIds.has(r[0])), provinces: coveredProvinces, cities: coveredCities };
      } else {
        data = index;
      }
      fill(region, 'Select region', data.regions.map(r => [r[0], r[1]]));
      if (region.options.length === 2) { region.selectedIndex = 1; setProvinces(); }
      setMode(!currentSaved, false);
    }).catch(fallback);
    setMode(!currentSaved, false);

    // If the address list cannot load, give back the plain address box so nobody gets stuck.
    function fallback() {
      wrapper.remove();
      if (sourceLabel) sourceLabel.hidden = false; else textarea.hidden = false;
      textarea.value = savedValue;
      textarea.required = true;
    }
  });
})();
