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
    const quick = el('input', { id: `${id}-quick`, type: 'search', autocomplete: 'off', placeholder: 'Start typing your city or municipality…' });
    const quickList = el('datalist', { id: `${id}-quick-list` });
    quick.setAttribute('list', quickList.id);
    const preview = el('p', { className: 'ph-address-preview', role: 'status' });
    preview.setAttribute('aria-live', 'polite');

    const controls = [region, province, city, barangay, houseNo, streetName, zip];
    fill(region, 'Loading regions…', []); fill(province, 'Select province', []); fill(city, 'Select city / municipality', []); fill(barangay, 'Select barangay', []);

    const picker = el('div', { className: 'ph-address-fields form-grid' }, [
      field('Quick find (optional)', quick, 'full ph-address-quick'), quickList,
      field('Region', region), field('Province', province),
      field('City / Municipality', city), field('Barangay', barangay),
      field('House / Unit / Bldg No.', houseNo), field('Street Name', streetName),
      field('Subdivision / Village (optional)', subdivision), field('ZIP code', zip),
      ...(withLandmark ? [field('Landmark (optional)', landmark, 'full')] : []),
      preview
    ]);
    const wrapper = el('fieldset', { className: 'ph-address full' }, [el('legend', { textContent: sourceLabel?.firstChild?.textContent?.trim() || 'Address' })]);

    // Booking: start from the account address and let the customer change it for this booking.
    let saved;
    if (savedValue) {
      const change = el('button', { type: 'button', className: 'button secondary', textContent: 'Change address' });
      const keep = el('button', { type: 'button', className: 'text-button', textContent: 'Use my saved address' });
      saved = el('div', { className: 'ph-address-saved' }, [el('div', {}, [el('span', { textContent: 'Saved address' }), el('strong', { textContent: savedValue })]), change]);
      picker.append(el('div', { className: 'full ph-address-keep' }, [keep]));
      change.addEventListener('click', () => setMode(true, true));
      keep.addEventListener('click', () => setMode(false, true));
      wrapper.append(saved);
    }
    wrapper.append(picker);
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
    const sync = () => {
      if (picker.hidden) { textarea.value = savedValue; return; }
      const complete = controls.every(control => control.value.trim()) && zip.validity.valid;
      textarea.value = complete ? compose() : '';
      preview.textContent = complete ? `Full address: ${textarea.value}` : 'Complete every required field to see the full address.';
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    };
    function setMode(editing, focus) {
      picker.hidden = !editing;
      if (saved) saved.hidden = editing;
      controls.forEach(control => { control.required = editing; });
      [...controls, subdivision, landmark, quick].forEach(control => { if (!editing) control.disabled = true; });
      if (editing) {
        region.disabled = !data; houseNo.disabled = streetName.disabled = subdivision.disabled = zip.disabled = landmark.disabled = quick.disabled = false;
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

    // Quick find: choosing "City, Province" fills region, province and city in one step.
    const quickMap = new Map();
    quick.addEventListener('input', () => {
      const match = quickMap.get(quick.value.trim().toLowerCase());
      if (!match) return;
      region.value = match.region; setProvinces();
      province.value = match.province; setCities();
      city.value = match.city;
      setBarangays().then(() => { sync(); barangay.focus(); });
      quick.value = '';
    });

    getIndex().then(index => {
      data = index;
      fill(region, 'Select region', data.regions.map(r => [r[0], r[1]]));
      const provinceOf = Object.fromEntries(data.provinces.map(p => [p[0], p]));
      quickList.replaceChildren(...data.cities.map(c => {
        const p = provinceOf[c[2]], label = p[1] === c[1] ? c[1] : `${c[1]}, ${p[1]}`;
        quickMap.set(label.toLowerCase(), { region: p[2], province: p[0], city: c[0] });
        return option(label, '');
      }));
      setMode(!savedValue, false);
    }).catch(fallback);
    setMode(!savedValue, false);

    // If the address list cannot load, give back the plain address box so nobody gets stuck.
    function fallback() {
      wrapper.remove();
      if (sourceLabel) sourceLabel.hidden = false; else textarea.hidden = false;
      textarea.value = savedValue;
      textarea.required = true;
    }
  });
})();
