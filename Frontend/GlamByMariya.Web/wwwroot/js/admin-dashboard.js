(() => {
  const dataEl = document.getElementById('dashboard-data');
  const root = document.getElementById('overview-dashboard');
  if (!dataEl || !root) return;

  let payload;
  try { payload = JSON.parse(dataEl.textContent || '{}'); } catch { return; }
  const bookings = Array.isArray(payload.bookings) ? payload.bookings : [];
  const nowDate = payload.now ? new Date(payload.now) : new Date();

  const php = new Intl.NumberFormat('en-PH', { style: 'currency', currency: 'PHP', maximumFractionDigits: 0 });
  const phpExact = new Intl.NumberFormat('en-PH', { style: 'currency', currency: 'PHP' });
  const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

  let range = '6m';
  let metric = 'revenue';

  const statusMeta = [
    { key: 'Confirmed', label: 'Confirmed', color: '#A6536B', filter: 'Confirmed' },
    { key: 'Completed', label: 'Completed', color: '#3E7C59', filter: 'Completed' },
    { key: 'Pending Payment', label: 'Pending Payment', color: '#C68A2B', filter: 'Pending Payment' },
    { key: 'Awaiting Approval', label: 'Awaiting Approval', color: '#8A5FA3', filter: 'Awaiting Approval' },
    { key: 'Closed', label: 'Cancelled / Other', color: '#C9B5BC', filter: 'Cancelled' }
  ];

  const filterBookings = () => {
    if (range === 'all') return bookings;
    const cutoff = range === 'year'
      ? new Date(nowDate.getFullYear(), 0, 1)
      : new Date(nowDate.getFullYear(), nowDate.getMonth() - 5, 1);
    return bookings.filter(b => new Date(b.start) >= cutoff);
  };

  const monthBuckets = filtered => {
    const months = [];
    if (range === '6m') {
      for (let i = 5; i >= 0; i--) {
        const d = new Date(nowDate.getFullYear(), nowDate.getMonth() - i, 1);
        months.push({ key: `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`, label: `${monthNames[d.getMonth()]} '${String(d.getFullYear()).slice(-2)}`, revenue: 0, count: 0 });
      }
    } else if (range === 'year') {
      for (let m = 0; m <= nowDate.getMonth(); m++) {
        months.push({ key: `${nowDate.getFullYear()}-${String(m + 1).padStart(2, '0')}`, label: monthNames[m], revenue: 0, count: 0 });
      }
      if (months.length < 6) {
        for (let m = months.length; m < 6; m++) {
          months.push({ key: `${nowDate.getFullYear()}-${String(m + 1).padStart(2, '0')}`, label: monthNames[m], revenue: 0, count: 0 });
        }
      }
    } else {
      const map = new Map();
      filtered.forEach(b => {
        const d = new Date(b.start);
        const key = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
        if (!map.has(key)) map.set(key, { key, label: `${monthNames[d.getMonth()]} '${String(d.getFullYear()).slice(-2)}`, revenue: 0, count: 0 });
      });
      if (map.size === 0) {
        const d = new Date(nowDate.getFullYear(), nowDate.getMonth(), 1);
        months.push({ key: `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`, label: `${monthNames[d.getMonth()]} '${String(d.getFullYear()).slice(-2)}`, revenue: 0, count: 0 });
      } else {
        [...map.values()].sort((a, b) => a.key.localeCompare(b.key)).slice(-12).forEach(m => months.push(m));
      }
    }
    const byKey = new Map(months.map(m => [m.key, m]));
    filtered.forEach(b => {
      const d = new Date(b.start);
      const key = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
      const bucket = byKey.get(key);
      if (bucket) {
        bucket.revenue += Number(b.collected || 0);
        bucket.count += 1;
      }
    });
    return months;
  };

  const renderTrend = filtered => {
    const host = root.querySelector('[data-chart="trend"]');
    const tooltip = root.querySelector('[data-trend-tooltip]');
    const summaryEl = root.querySelector('[data-trend-summary]');
    if (!host) return;
    const buckets = monthBuckets(filtered);
    const values = buckets.map(b => metric === 'revenue' ? b.revenue : b.count);
    const maxVal = Math.max(...values, metric === 'revenue' ? 1000 : 4);
    const totalRev = buckets.reduce((s, b) => s + b.revenue, 0);
    const totalCount = buckets.reduce((s, b) => s + b.count, 0);
    if (summaryEl) {
      summaryEl.textContent = metric === 'revenue'
        ? `${phpExact.format(totalRev)} verified across ${totalCount} booking${totalCount === 1 ? '' : 's'}`
        : `${totalCount} booking${totalCount === 1 ? '' : 's'} (${phpExact.format(totalRev)} verified)`;
    }

    const W = 560, H = 220, padL = 62, padR = 16, padT = 18, padB = 34;
    const plotW = W - padL - padR, plotH = H - padT - padB;
    const step = plotW / Math.max(buckets.length, 1);
    const barW = Math.min(44, Math.max(18, step * 0.54));

    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
    svg.setAttribute('class', 'dash-svg');
    svg.setAttribute('role', 'img');
    svg.setAttribute('aria-label', metric === 'revenue' ? 'Monthly verified collections bar chart' : 'Monthly bookings bar chart');

    for (let i = 0; i <= 4; i++) {
      const y = padT + (plotH * i) / 4;
      const val = maxVal * (1 - i / 4);
      const line = document.createElementNS(ns, 'line');
      line.setAttribute('x1', String(padL)); line.setAttribute('x2', String(W - padR));
      line.setAttribute('y1', String(y)); line.setAttribute('y2', String(y));
      line.setAttribute('class', 'dash-grid-line');
      svg.append(line);

      const label = document.createElementNS(ns, 'text');
      label.setAttribute('x', String(padL - 8)); label.setAttribute('y', String(y + 4));
      label.setAttribute('text-anchor', 'end'); label.setAttribute('class', 'dash-axis-label');
      label.textContent = metric === 'revenue' ? (val >= 1000 ? `₱${(val / 1000).toFixed(val >= 10000 ? 0 : 1)}k` : php.format(val)) : String(Math.round(val));
      svg.append(label);
    }

    buckets.forEach((b, idx) => {
      const v = metric === 'revenue' ? b.revenue : b.count;
      const h = maxVal > 0 ? Math.max(v > 0 ? 6 : 2, (v / maxVal) * plotH) : 2;
      const cx = padL + step * idx + step / 2;
      const x = cx - barW / 2;
      const y = padT + plotH - h;

      const rect = document.createElementNS(ns, 'rect');
      rect.setAttribute('x', String(x)); rect.setAttribute('y', String(y));
      rect.setAttribute('width', String(barW)); rect.setAttribute('height', String(h));
      rect.setAttribute('rx', '6');
      rect.setAttribute('class', v > 0 ? 'dash-bar' : 'dash-bar is-zero');
      rect.setAttribute('tabindex', '0');
      const tipText = `${b.label}: ${phpExact.format(b.revenue)} · ${b.count} booking${b.count === 1 ? '' : 's'}`;
      const showTip = () => { if (tooltip) { tooltip.textContent = tipText; tooltip.classList.add('is-active'); } };
      rect.addEventListener('mouseenter', showTip);
      rect.addEventListener('focus', showTip);
      svg.append(rect);

      const xLbl = document.createElementNS(ns, 'text');
      xLbl.setAttribute('x', String(cx)); xLbl.setAttribute('y', String(H - 10));
      xLbl.setAttribute('text-anchor', 'middle'); xLbl.setAttribute('class', 'dash-axis-label');
      xLbl.textContent = b.label;
      svg.append(xLbl);
    });

    host.replaceChildren(svg);
    if (tooltip) {
      const latest = [...buckets].reverse().find(b => b.count > 0) || buckets[buckets.length - 1];
      tooltip.textContent = latest ? `${latest.label}: ${phpExact.format(latest.revenue)} · ${latest.count} booking${latest.count === 1 ? '' : 's'}` : 'Hover any bar for details.';
    }
  };

  const renderStatusDonut = filtered => {
    const host = root.querySelector('[data-chart="status"]');
    const legend = root.querySelector('[data-status-legend]');
    if (!host || !legend) return;

    const counts = statusMeta.map(m => ({
      ...m,
      count: filtered.filter(b => m.key === 'Closed'
        ? !['Confirmed', 'Completed', 'Pending Payment', 'Awaiting Approval'].includes(b.status)
        : b.status === m.key).length
    }));
    const total = counts.reduce((s, c) => s + c.count, 0);

    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 180 180');
    svg.setAttribute('class', 'dash-donut-svg');
    svg.setAttribute('role', 'img');
    svg.setAttribute('aria-label', 'Booking status distribution donut chart');

    const r = 64, circ = 2 * Math.PI * r;
    const bg = document.createElementNS(ns, 'circle');
    bg.setAttribute('cx', '90'); bg.setAttribute('cy', '90'); bg.setAttribute('r', String(r));
    bg.setAttribute('fill', 'none'); bg.setAttribute('stroke', '#F1E6E2'); bg.setAttribute('stroke-width', '20');
    svg.append(bg);

    const centerVal = document.createElementNS(ns, 'text');
    centerVal.setAttribute('x', '90'); centerVal.setAttribute('y', '87');
    centerVal.setAttribute('text-anchor', 'middle'); centerVal.setAttribute('class', 'dash-donut-num');
    centerVal.textContent = String(total);

    const centerSub = document.createElementNS(ns, 'text');
    centerSub.setAttribute('x', '90'); centerSub.setAttribute('y', '107');
    centerSub.setAttribute('text-anchor', 'middle'); centerSub.setAttribute('class', 'dash-donut-sub');
    centerSub.textContent = total === 1 ? 'booking' : 'bookings';

    let offset = 0;
    counts.forEach(item => {
      if (item.count <= 0 || total === 0) return;
      const frac = item.count / total;
      const seg = document.createElementNS(ns, 'circle');
      seg.setAttribute('cx', '90'); seg.setAttribute('cy', '90'); seg.setAttribute('r', String(r));
      seg.setAttribute('fill', 'none'); seg.setAttribute('stroke', item.color); seg.setAttribute('stroke-width', '20');
      seg.setAttribute('stroke-dasharray', `${frac * circ} ${circ}`);
      seg.setAttribute('stroke-dashoffset', String(-offset));
      seg.setAttribute('transform', 'rotate(-90 90 90)');
      seg.setAttribute('class', 'dash-donut-seg');
      seg.addEventListener('mouseenter', () => { centerVal.textContent = String(item.count); centerSub.textContent = item.label; });
      seg.addEventListener('mouseleave', () => { centerVal.textContent = String(total); centerSub.textContent = total === 1 ? 'booking' : 'bookings'; });
      svg.append(seg);
      offset += frac * circ;
    });
    svg.append(centerVal, centerSub);
    host.replaceChildren(svg);

    legend.replaceChildren();
    counts.forEach(item => {
      const pct = total > 0 ? Math.round((item.count / total) * 100) : 0;
      const a = document.createElement('a');
      a.className = 'dash-legend-row';
      a.href = `/Admin/Appointments?mode=list&status=${encodeURIComponent(item.filter)}`;
      a.innerHTML = `<span class="dash-swatch" style="background:${item.color}"></span><span class="dash-legend-label"></span><strong class="dash-legend-val"></strong>`;
      a.querySelector('.dash-legend-label').textContent = item.label;
      a.querySelector('.dash-legend-val').textContent = `${item.count} (${pct}%)`;
      legend.append(a);
    });
  };

  const renderServices = filtered => {
    const host = root.querySelector('[data-chart="services"]');
    if (!host) return;
    const map = new Map();
    filtered.forEach(b => {
      const name = b.service || 'Makeup Service';
      const entry = map.get(name) || { name, count: 0, revenue: 0 };
      entry.count += 1;
      entry.revenue += Number(b.collected || 0);
      map.set(name, entry);
    });
    const rows = [...map.values()].sort((a, b) => (b.revenue - a.revenue) || (b.count - a.count)).slice(0, 5);
    host.replaceChildren();
    if (!rows.length) {
      const empty = document.createElement('p');
      empty.className = 'help';
      empty.textContent = 'No bookings in this period yet.';
      host.append(empty);
      return;
    }
    const maxRev = Math.max(...rows.map(r => r.revenue), 1);
    const maxCnt = Math.max(...rows.map(r => r.count), 1);
    rows.forEach(r => {
      const pct = Math.max(6, Math.round((r.revenue > 0 ? r.revenue / maxRev : r.count / maxCnt) * 100));
      const row = document.createElement('div');
      row.className = 'dash-service-row';
      row.innerHTML = `<div class="dash-service-head"><strong class="dash-service-name"></strong><span class="dash-service-meta"></span></div><div class="dash-service-track"><span class="dash-service-fill"></span></div>`;
      row.querySelector('.dash-service-name').textContent = r.name;
      row.querySelector('.dash-service-meta').textContent = `${phpExact.format(r.revenue)} · ${r.count} booking${r.count === 1 ? '' : 's'}`;
      row.querySelector('.dash-service-fill').style.width = `${pct}%`;
      host.append(row);
    });
  };

  const renderAll = () => {
    const filtered = filterBookings();
    renderTrend(filtered);
    renderStatusDonut(filtered);
    renderServices(filtered);
  };

  root.querySelectorAll('[data-dash-range]').forEach(btn => {
    btn.addEventListener('click', () => {
      range = btn.dataset.dashRange;
      root.querySelectorAll('[data-dash-range]').forEach(b => b.setAttribute('aria-pressed', String(b === btn)));
      renderAll();
    });
  });
  root.querySelectorAll('[data-dash-metric]').forEach(btn => {
    btn.addEventListener('click', () => {
      metric = btn.dataset.dashMetric;
      root.querySelectorAll('[data-dash-metric]').forEach(b => b.setAttribute('aria-pressed', String(b === btn)));
      renderAll();
    });
  });

  renderAll();
})();
