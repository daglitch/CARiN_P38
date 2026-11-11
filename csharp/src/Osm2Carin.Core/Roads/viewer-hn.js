// House numbers (Phase 3d) for the road-tile viewer. The export adds this script to the page; it reads the
// street cells' "hn": [line, side A start, side B start, side A end, side B end, scheme], as in the 0x04
// blocks on the disc. Side A is the left of the line from its start to its end. 0x7FFF = no number.
(() => {
  const NONE = 0x7FFF, MINZ = 15, LABELZ = 18, COLOR = '#7b3294';
  const SCHEME = {0: 'none', 1: 'mixed', 2: 'odd / even by side'};
  let on = true, sel = null;

  // keep each cell's house numbers with its lines
  const build0 = build;
  build = (j, delta) => {
    const r = build0(j, delta);
    if (j.hn) r.hn = new Map(j.hn.map(h => [h[0], h.slice(1)]));
    return r;
  };

  const lab = document.createElement('label');
  lab.innerHTML = `<input type="checkbox" checked> <span class="sw" style="background:${COLOR}"></span>House numbers (zoom ${MINZ}+, click a street)`;
  document.getElementById('osm').parentElement.before(lab);
  lab.querySelector('input').onchange = e => { on = e.target.checked; if (!on) map.closePopup(); layer.redraw(); };

  // screen points of a cell line
  function pts(L_, i, ox, oy, sc) {
    const o = [];
    for (let p = L_.start[i]; p < L_.start[i + 1]; p += 2) o.push([(L_.xy[p] - ox) * sc, (L_.xy[p + 1] - oy) * sc]);
    return o;
  }
  // the point at fraction f of the line's length, and the unit normal to its left (screen y points down)
  function at(P, f) {
    let len = 0;
    for (let i = 1; i < P.length; i++) len += Math.hypot(P[i][0] - P[i - 1][0], P[i][1] - P[i - 1][1]);
    let want = len * f;
    for (let i = 1; i < P.length; i++) {
      const dx = P[i][0] - P[i - 1][0], dy = P[i][1] - P[i - 1][1], d = Math.hypot(dx, dy);
      if (d > 0 && (want <= d || i === P.length - 1)) {
        const t = Math.min(want / d, 1);
        return {x: P[i - 1][0] + dx * t, y: P[i - 1][1] + dy * t, nx: dy / d, ny: -dx / d, len};
      }
      want -= d;
    }
    return {x: P[0][0], y: P[0][1], nx: 0, ny: -1, len};
  }
  const num = v => v === NONE ? null : v;
  const range = (a, b) => num(a) == null && num(b) == null ? null : num(a) === num(b) || num(b) == null ? `${num(a)}` : num(a) == null ? `${num(b)}` : `${a}–${b}`;

  function view() {
    const z = map.getZoom(), sc = Math.pow(2, z), o = map.getPixelBounds().min, size = map.getSize();
    const ox = o.x / sc, oy = o.y / sc;
    return {z, sc, ox, oy, vx1: ox + size.x / sc, vy1: oy + size.y / sc, size};
  }
  function* visible(v) {
    for (const k in cells) {
      const L_ = cells[k];
      if (!L_.hn) continue;
      for (const [i, h] of L_.hn) {
        const b = i * 4;
        if (L_.bb[b + 2] < v.ox || L_.bb[b] > v.vx1 || L_.bb[b + 3] < v.oy || L_.bb[b + 1] > v.vy1) continue;
        yield [k, L_, i, h];
      }
    }
  }

  const Layer = L.Layer.extend({
    onAdd(m) {
      this._c = L.DomUtil.create('canvas', 'leaflet-zoom-hide');
      this._c.style.zIndex = 1;                         // above the roads' canvas
      m.getPanes().overlayPane.appendChild(this._c);
      this.redraw();                                    // and again with every redraw of the roads
    },
    redraw() {
      const c = this._c;
      if (!c) return;
      const v = view(), dpr = window.devicePixelRatio || 1;
      c.width = v.size.x * dpr; c.height = v.size.y * dpr;
      c.style.width = v.size.x + 'px'; c.style.height = v.size.y + 'px';
      L.DomUtil.setPosition(c, map.containerPointToLayerPoint([0, 0]));
      const g = c.getContext('2d'); g.setTransform(dpr, 0, 0, dpr, 0, 0);
      if (!on || !enabled[0] || v.z < MINZ) return;
      g.lineJoin = 'round'; g.lineCap = 'round';
      const labels = [];
      let n = 0;
      g.strokeStyle = COLOR; g.lineWidth = 3; g.beginPath();
      for (const [k, L_, i, h] of visible(v)) {
        const P = pts(L_, i, v.ox, v.oy, v.sc);
        g.moveTo(P[0][0], P[0][1]);
        for (let p = 1; p < P.length; p++) g.lineTo(P[p][0], P[p][1]);
        n++;
        if (v.z >= LABELZ) labels.push([P, h]);
      }
      g.stroke();
      if (sel && cells[sel[0]]) {
        const P = pts(cells[sel[0]], sel[1], v.ox, v.oy, v.sc);
        g.strokeStyle = '#000'; g.lineWidth = 5; g.beginPath();
        g.moveTo(P[0][0], P[0][1]);
        for (let p = 1; p < P.length; p++) g.lineTo(P[p][0], P[p][1]);
        g.stroke();
        g.fillStyle = '#000'; g.beginPath(); g.arc(P[0][0], P[0][1], 5, 0, 2 * Math.PI); g.fill();
      }
      g.font = '11px system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle';
      g.lineWidth = 3; g.strokeStyle = '#fff'; g.fillStyle = COLOR;
      const put = (s, q, side) => {
        if (s == null) return;
        const x = q.x + side * q.nx * 10, y = q.y + side * q.ny * 10;
        g.strokeText(s, x, y); g.fillText(s, x, y);
      };
      for (const [P, h] of labels) {
        const m = at(P, 0.5);
        if (m.len < 90) { put(range(h[0], h[2]), m, 1); put(range(h[1], h[3]), m, -1); continue; }
        const a = at(P, 0.15), b = at(P, 0.85);
        put(num(h[0]) == null ? null : `${h[0]}`, a, 1); put(num(h[2]) == null ? null : `${h[2]}`, b, 1);
        put(num(h[1]) == null ? null : `${h[1]}`, a, -1); put(num(h[3]) == null ? null : `${h[3]}`, b, -1);
      }
      const st = document.getElementById('status');
      st.textContent = st.textContent.replace(/ · \d[\d,.\s]* with house numbers.*$/, '') +
        ` · ${n.toLocaleString()} with house numbers` + (v.z < LABELZ ? ` (labels at zoom ${LABELZ}+)` : '');
    },
  });
  const layer = new Layer();
  layer.addTo(map);
  // redraw with the roads, e.g. when new street cells arrive
  const redraw0 = roads.redraw;
  roads.redraw = function () { redraw0.call(this); layer.redraw(); };

  // click: the nearest street with house numbers within 14 px
  map.on('click', e => {
    const v = view();
    if (!on || v.z < MINZ) return;
    const cp = map.latLngToContainerPoint(e.latlng);
    let best = null, bd = 14;
    for (const [k, L_, i, h] of visible(v)) {
      const P = pts(L_, i, v.ox, v.oy, v.sc);
      for (let p = 1; p < P.length; p++) {
        const [x0, y0] = P[p - 1], [x1, y1] = P[p], dx = x1 - x0, dy = y1 - y0, l2 = dx * dx + dy * dy;
        const t = l2 ? Math.max(0, Math.min(1, ((cp.x - x0) * dx + (cp.y - y0) * dy) / l2)) : 0;
        const d = Math.hypot(cp.x - x0 - t * dx, cp.y - y0 - t * dy);
        if (d < bd) { bd = d; best = [k, i, h]; }
      }
    }
    sel = best && [best[0], best[1]];
    layer.redraw();
    if (!best) return;
    const h = best[2];
    const side = (s, a, b) => `<tr><td>Side ${s}</td><td>${range(a, b) ?? '—'}</td><td>${num(a) ?? '—'} → ${num(b) ?? '—'}</td></tr>`;
    L.popup().setLatLng(e.latlng).setContent(
      `<b>House numbers</b> (0x04 record)<table style="margin-top:4px">` +
      `<tr><td></td><td>range</td><td>start → end</td></tr>` +
      side('A (left)', h[0], h[2]) + side('B (right)', h[1], h[3]) + `</table>` +
      `<div>Scheme ${h[4]}: ${SCHEME[h[4]] ?? '?'}</div>` +
      `<div style="color:#666;margin-top:4px">The dot marks the start; left and right are as seen from it.</div>`)
      .on('remove', () => { sel = null; layer.redraw(); })
      .openOn(map);
  });
})();
