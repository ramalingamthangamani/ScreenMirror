'use strict';
/* ScreenMirror phone/tablet client: pairing, frame rendering, touch/trackpad/mouse/keyboard input. */

const $ = (s) => document.querySelector(s);
const now = () => performance.now();
const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);

const store = {
  get(k, d) { try { const v = localStorage.getItem('sm.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('sm.' + k, JSON.stringify(v)); } catch { /* private mode */ } },
  del(k) { try { localStorage.removeItem('sm.' + k); } catch { /* ignore */ } },
};

const prefs = {
  mode: store.get('mode', 'touch'),         // 'touch' (direct) | 'trackpad' (relative)
  speed: store.get('speed', 1.6),
  stats: store.get('stats', true),
  monitor: store.get('monitor', null),
  toolbar: store.get('toolbar', true),
};

// ======================================================================== pairing

const params = new URLSearchParams(location.search);
let qrCode = params.get('code');
if (qrCode) history.replaceState(null, '', location.pathname);

function defaultName() {
  const ua = navigator.userAgent;
  const m = ua.match(/Android [\d.]+; ([^;)]+?)(?: Build|\))/);
  if (m && m[1].trim().length > 1) return m[1].trim();
  if (/iPad|Macintosh/.test(ua) && navigator.maxTouchPoints > 1) return 'iPad';
  if (/iPhone/.test(ua)) return 'iPhone';
  if (/Android/.test(ua)) return /Mobile/.test(ua) ? 'Android phone' : 'Android tablet';
  if (/Windows/.test(ua)) return 'Windows device';
  if (/Mac/.test(ua)) return 'Mac';
  return 'Device';
}

function pairMessage(text, isError) {
  const el = $('#pairMsg');
  el.textContent = text || '';
  el.classList.toggle('error', !!isError);
}

function showPair(message, isError) {
  $('#viewer').hidden = true;
  $('#pair').hidden = false;
  $('#devName').value = $('#devName').value || store.get('name', defaultName());
  if (qrCode) $('#code').value = qrCode;
  pairMessage(message, isError);
}

async function postJson(url, body) {
  const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `Request failed (${res.status})`);
  return data;
}

function paired(token) {
  store.set('token', token);
  store.set('name', $('#devName').value.trim());
  qrCode = null;
  startViewer();
}

async function pairWithCode(code) {
  const btn = $('#pairBtn');
  btn.disabled = true;
  pairMessage('Pairing…');
  try {
    const { token } = await postJson('/api/pair', { code, name: $('#devName').value.trim() || defaultName() });
    paired(token);
  } catch (err) {
    qrCode = null;
    pairMessage(err.message, true);
  } finally {
    btn.disabled = false;
  }
}

let requestTimer = null;
let requestId = null;
async function requestAccess() {
  const btn = $('#requestBtn');
  if (requestTimer) {
    clearTimeout(requestTimer);
    requestTimer = null;
    if (requestId) fetch(`/api/request/${encodeURIComponent(requestId)}`, { method: 'DELETE' }).catch(() => {});
    requestId = null;
    btn.textContent = 'Ask the PC for access';
    pairMessage('');
    return;
  }
  try {
    const { id } = await postJson('/api/request', { name: $('#devName').value.trim() || defaultName() });
    requestId = id;
    btn.textContent = 'Cancel request';
    pairMessage('Waiting for approval — click “Approve” in the dashboard on your PC.');
    const poll = async () => {
      try {
        const r = await (await fetch(`/api/request/${encodeURIComponent(id)}`)).json();
        if (requestId !== id) return;   // cancelled meanwhile
        if (r.status === 'pending') { requestTimer = setTimeout(poll, 1000); return; }
        requestTimer = null;
        requestId = null;
        btn.textContent = 'Ask the PC for access';
        if (r.status === 'approved') paired(r.token);
        else pairMessage(r.status === 'denied' ? 'The PC declined the request.' : 'The request expired. Try again.', true);
      } catch {
        if (requestId === id) requestTimer = setTimeout(poll, 2000);
      }
    };
    requestTimer = setTimeout(poll, 1000);
  } catch (err) {
    pairMessage(err.message, true);
  }
}

$('#pairForm').addEventListener('submit', (e) => {
  e.preventDefault();
  const code = $('#code').value.replace(/\D/g, '');
  if (code.length !== 6) { pairMessage('Enter the 6-digit code shown on your PC.', true); return; }
  pairWithCode(code);
});
$('#code').addEventListener('input', (e) => { e.target.value = e.target.value.replace(/\D/g, '').slice(0, 6); });
$('#requestBtn').addEventListener('click', requestAccess);

// ======================================================================== connection

let ws = null;
let retry = 0;
let reconnectTimer = null;
let paused = false;            // user paused on this device
let hostStreaming = true;
let canControl = true;
let monitors = [];
let currentMonitor = null;

function wsSend(msg) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify(msg));
}

function connect() {
  clearTimeout(reconnectTimer);
  if (ws) return;
  overlay(retry ? 'Reconnecting…' : 'Connecting…', true);
  const sock = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`);
  sock.binaryType = 'arraybuffer';
  ws = sock;
  sock.onopen = () => {
    sock.send(JSON.stringify({ t: 'auth', token: store.get('token'), monitor: prefs.monitor, paused: paused || document.hidden }));
  };
  sock.onmessage = (e) => {
    if (typeof e.data === 'string') onMessage(JSON.parse(e.data));
    else onFrame(e.data);
  };
  sock.onclose = (e) => {
    if (ws !== sock) return;
    ws = null;
    stats.rtt = null;
    renderStats();
    if (e.code === 4001) {          // pairing missing / revoked
      store.del('token');
      showPair(e.reason === 'Pairing removed by host' ? 'This device was removed on the PC. Pair again to reconnect.' : '', true);
      if (qrCode) pairWithCode(qrCode);
      return;
    }
    if (e.code === 4000) {          // kicked by host: don't auto-reconnect
      overlay('Disconnected by the PC', false, 'Reconnect');
      return;
    }
    retry++;
    overlay('Connection lost — reconnecting…', true);
    reconnectTimer = setTimeout(connect, Math.min(5000, 400 * 2 ** Math.min(retry, 4)));
  };
}

function onMessage(m) {
  switch (m.t) {
    case 'hello':
      retry = 0;
      frame.lastId = 0;            // frame ids restart when the host restarts
      monitors = m.monitors;
      currentMonitor = m.monitor;
      $('#sheetInfo').textContent = `${m.name} → ${m.host}`;
      fillMonitors();
      overlay(null);
      updateOverlay();
      break;
    case 'monitors':
      monitors = m.monitors;
      currentMonitor = m.monitor;
      fillMonitors();
      break;
    case 'state':
      hostStreaming = m.streaming;
      canControl = m.control;
      $('#hudBadge').hidden = canControl || !hostStreaming;
      updateOverlay();
      break;
    case 'cur':
      cursor.x = m.x; cursor.y = m.y; cursor.v = m.v;
      if (prefs.mode === 'trackpad') followCursor();
      placeCursor();
      break;
    case 'pong':
      if (typeof m.id === 'number') {
        const rtt = now() - m.id;
        stats.rtt = stats.rtt == null ? rtt : stats.rtt * 0.7 + rtt * 0.3;
      }
      break;
    case 'stats':
      stats.serverFps = m.fps; stats.kbps = m.kbps; stats.latency = m.latency;
      renderStats();
      break;
  }
}

function overlay(text, spinning, buttonLabel) {
  $('#overlay').hidden = !text;
  if (!text) return;
  $('#overlayText').textContent = text;
  $('#spinner').hidden = !spinning;
  $('#overlayBtn').hidden = !buttonLabel;
  if (buttonLabel) $('#overlayBtn').textContent = buttonLabel;
}

function updateOverlay() {
  if (!ws || ws.readyState !== 1) return;
  if (!hostStreaming) overlay('Streaming is stopped on the PC', false);
  else if (paused) overlay('Stream paused', false, 'Resume');
  else overlay(null);
}

$('#overlayBtn').addEventListener('click', () => {
  if (!ws) { retry = 0; connect(); return; }
  if (paused) setPaused(false);
});

// ======================================================================== stats

const stats = { frames: 0, fps: 0, rtt: null, latency: null, serverFps: 0, kbps: 0 };

function quality() {
  const { rtt, latency } = stats;
  if (rtt == null || latency == null) return 'unknown';
  if (rtt < 25 && latency < 100) return 'excellent';
  if (rtt < 60 && latency < 180) return 'good';
  if (rtt < 150 && latency < 350) return 'fair';
  return 'poor';
}

function displayLatency() {
  // Server measures capture -> painted here -> ack received; remove the ack's trip back.
  if (stats.latency == null) return null;
  return Math.max(1, Math.round(stats.latency - (stats.rtt || 0) / 2));
}

function renderStats() {
  const q = ws ? quality() : 'poor';
  const hud = $('#hud');
  hud.className = `hud q-${q}`;
  hud.hidden = !prefs.stats;
  const lat = displayLatency();
  const fpsText = stats.fps ? `${stats.fps} fps` : 'idle';
  $('#hudText').textContent = ws ? `${fpsText} · ${lat == null ? '—' : lat + ' ms'}` : 'offline';
  $('#dQuality').textContent = q[0].toUpperCase() + q.slice(1);
  $('#dFps').textContent = `${stats.fps}`;
  $('#dLat').textContent = lat == null ? '—' : `${lat} ms`;
  $('#dPing').textContent = stats.rtt == null ? '—' : `${Math.round(stats.rtt)} ms`;
  $('#dRate').textContent = `${(stats.kbps / 1000).toFixed(1)} Mbps`;
  $('#dRes').textContent = frame.w ? `${frame.w}×${frame.h}` : '—';
}

setInterval(() => {
  stats.fps = stats.frames;
  stats.frames = 0;
  wsSend({ t: 'ping', id: now() });
  wsSend({ t: 'cstat', fps: stats.fps, rtt: stats.rtt == null ? null : Math.round(stats.rtt) });
  renderStats();
}, 1000);

$('#hud').addEventListener('click', () => { $('#hudDetail').hidden = !$('#hudDetail').hidden; });

// ======================================================================== rendering

const canvas = $('#screen');
const ctx = canvas.getContext('2d', { alpha: false });
const cursorEl = $('#cursor');
const frame = { img: null, w: 0, h: 0, lastId: 0 };
const cursor = { x: 0, y: 0, v: false };
let view = { x: 0, y: 0, w: 0, h: 0 };   // where the frame is drawn, CSS px
let pendingAcks = [];
let drawQueued = false;
let dpr = 1;

function stageSize() { return { w: canvas.clientWidth, h: canvas.clientHeight }; }

function fitRect() {
  const { w, h } = stageSize();
  if (!frame.w) return { x: 0, y: 0, w, h };
  const s = Math.min(w / frame.w, h / frame.h);
  return { x: (w - frame.w * s) / 2, y: (h - frame.h * s) / 2, w: frame.w * s, h: frame.h * s };
}

function resetView() { view = fitRect(); requestDraw(); }
function isZoomed() { return view.w > fitRect().w * 1.01; }

function clampView() {
  const { w, h } = stageSize();
  view.x = view.w <= w ? (w - view.w) / 2 : clamp(view.x, w - view.w, 0);
  view.y = view.h <= h ? (h - view.h) / 2 : clamp(view.y, h - view.h, 0);
  $('#zoomReset').hidden = !isZoomed();
  requestDraw();
}

function zoomAt(px, py, factor) {
  const fit = fitRect();
  const nw = clamp(view.w * factor, fit.w, fit.w * 6);
  const f = nw / view.w;
  view.x = px - (px - view.x) * f;
  view.y = py - (py - view.y) * f;
  view.w = nw;
  view.h *= f;
  clampView();
}

function panBy(dx, dy) { view.x += dx; view.y += dy; clampView(); }

function followCursor() {
  if (!isZoomed() || !cursor.v) return;
  const { w, h } = stageSize();
  const cx = view.x + cursor.x * view.w, cy = view.y + cursor.y * view.h, m = 60;
  let dx = 0, dy = 0;
  if (cx < m) dx = m - cx; else if (cx > w - m) dx = w - m - cx;
  if (cy < m) dy = m - cy; else if (cy > h - m) dy = h - m - cy;
  if (dx || dy) panBy(dx, dy);
}

function resize() {
  dpr = Math.min(window.devicePixelRatio || 1, 3);
  const { w, h } = stageSize();
  canvas.width = Math.round(w * dpr);
  canvas.height = Math.round(h * dpr);
  resetView();
}
window.addEventListener('resize', resize);

function requestDraw() {
  if (drawQueued) return;
  drawQueued = true;
  requestAnimationFrame(draw);
}

function draw() {
  drawQueued = false;
  ctx.fillStyle = '#000';
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  if (frame.img) {
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(frame.img, view.x * dpr, view.y * dpr, view.w * dpr, view.h * dpr);
  }
  placeCursor();
  for (const id of pendingAcks) wsSend({ t: 'ack', id });
  pendingAcks = [];
}

function placeCursor() {
  const show = cursor.v && frame.img && hostStreaming;
  cursorEl.classList.toggle('off', !show);
  if (show) cursorEl.style.transform = `translate(${view.x + cursor.x * view.w}px, ${view.y + cursor.y * view.h}px)`;
}

const decodeJpeg = typeof createImageBitmap === 'function'
  ? (blob) => createImageBitmap(blob)
  : (blob) => new Promise((resolve, reject) => {
      const img = new Image();
      const url = URL.createObjectURL(blob);
      img.onload = () => { URL.revokeObjectURL(url); resolve(img); };
      img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('decode')); };
      img.src = url;
    });

function onFrame(buf) {
  const dv = new DataView(buf);
  const id = dv.getUint32(0, true);
  const w = dv.getUint16(12, true), h = dv.getUint16(14, true);
  decodeJpeg(new Blob([new Uint8Array(buf, 16)], { type: 'image/jpeg' })).then((img) => {
    if (id < frame.lastId) { img.close?.(); pendingAcks.push(id); return; }   // out-of-order; drop
    const aspectChanged = !frame.w || Math.abs(w / h - frame.w / frame.h) > 0.01;
    frame.img?.close?.();
    Object.assign(frame, { img, w, h, lastId: id });
    if (aspectChanged) resetView();
    stats.frames++;
    pendingAcks.push(id);
    draw();   // paint now rather than on the next animation frame: lower latency, acks never stall
  }).catch(() => wsSend({ t: 'ack', id }));
}

// ======================================================================== pointer input

const WHEEL_PER_PX = 2.4;     // two-finger scroll speed
const TAP_SLOP = 10;          // px a finger may wiggle and still count as a tap
const LONG_PRESS_MS = 520;

let moveQueued = null;        // latest absolute position (normalized)
let rel = { x: 0, y: 0 };     // accumulated relative motion (desktop px)
let wheelAcc = { x: 0, y: 0 };
let flushQueued = false;

function toNorm(px, py) {
  return { x: +((px - view.x) / view.w).toFixed(5), y: +((py - view.y) / view.h).toFixed(5) };
}

function scheduleFlush() {
  if (flushQueued) return;
  flushQueued = true;
  requestAnimationFrame(flushInput);
}

function flushInput() {
  flushQueued = false;
  if (moveQueued) { wsSend({ t: 'move', ...moveQueued }); moveQueued = null; }
  const rx = Math.trunc(rel.x), ry = Math.trunc(rel.y);
  if (rx || ry) { wsSend({ t: 'rel', dx: rx, dy: ry }); rel.x -= rx; rel.y -= ry; }
  const wx = Math.trunc(wheelAcc.x), wy = Math.trunc(wheelAcc.y);
  if (wx || wy) { wsSend({ t: 'wheel', dx: wx, dy: wy }); wheelAcc.x -= wx; wheelAcc.y -= wy; }
}

function sendNow(msg) { flushInput(); wsSend(msg); }
function queueMove(n) { moveQueued = n; scheduleFlush(); }
function queueRel(dx, dy) { rel.x += dx; rel.y += dy; scheduleFlush(); }
function queueWheel(dx, dy) { wheelAcc.x += dx; wheelAcc.y += dy; scheduleFlush(); }

function click(button, at) {
  const pos = at ? toNorm(at.x, at.y) : {};
  sendNow({ t: 'down', b: button, ...pos });
  sendNow({ t: 'up', b: button, ...pos });
}

function buzz() { navigator.vibrate?.(12); }
function inputAllowed() { return canControl && hostStreaming && !paused && frame.img; }

const pts = new Map();
let g = null;                 // current touch gesture
let lastTap = null;

function centroid() {
  const a = [...pts.values()];
  return { x: (a[0].x + a[1].x) / 2, y: (a[0].y + a[1].y) / 2, d: Math.hypot(a[0].x - a[1].x, a[0].y - a[1].y) };
}

function capture(e) { try { canvas.setPointerCapture(e.pointerId); } catch { /* pointer already gone */ } }

canvas.addEventListener('contextmenu', (e) => e.preventDefault());

canvas.addEventListener('pointerdown', (e) => {
  if (!keybar.hidden) e.preventDefault();   // keep the soft keyboard open while tapping the screen
  if (e.pointerType === 'mouse') {
    if (!inputAllowed()) return;
    capture(e);
    sendNow({ t: 'down', b: e.button === 1 ? 1 : e.button === 2 ? 2 : 0, ...toNorm(e.offsetX, e.offsetY) });
    return;
  }
  capture(e);
  pts.set(e.pointerId, { x: e.offsetX, y: e.offsetY });
  if (pts.size === 1) startOne(e.offsetX, e.offsetY);
  else if (pts.size === 2) startTwo();
});

canvas.addEventListener('pointermove', (e) => {
  if (e.pointerType === 'mouse') {
    if (inputAllowed()) queueMove(toNorm(e.offsetX, e.offsetY));
    return;
  }
  const p = pts.get(e.pointerId);
  if (!p) return;
  const prev = { x: p.x, y: p.y };
  p.x = e.offsetX; p.y = e.offsetY;
  if (!g) return;
  if (g.kind === 'one') moveOne(p, prev, e.timeStamp);
  else if (g.kind === 'two' && pts.size === 2) moveTwo();
});

function pointerEnd(e) {
  if (e.pointerType === 'mouse') {
    if (e.type === 'pointerup' && inputAllowed()) sendNow({ t: 'up', b: e.button === 1 ? 1 : e.button === 2 ? 2 : 0, ...toNorm(e.offsetX, e.offsetY) });
    return;
  }
  if (!pts.has(e.pointerId)) return;
  if (g?.kind === 'one') endOne(e.type === 'pointercancel');
  else if (g?.kind === 'two') endTwo();
  pts.delete(e.pointerId);
  if (pts.size === 0) g = null;
}
canvas.addEventListener('pointerup', pointerEnd);
canvas.addEventListener('pointercancel', pointerEnd);

canvas.addEventListener('wheel', (e) => {
  e.preventDefault();
  if (e.ctrlKey) { zoomAt(e.offsetX, e.offsetY, Math.exp(-e.deltaY / 200)); return; }   // trackpad pinch
  if (!inputAllowed()) return;
  const k = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 400 : 1.2;
  queueWheel(e.deltaX * k, -e.deltaY * k);
}, { passive: false });

// ----- one finger
function startOne(x, y) {
  g = { kind: 'one', sx: x, sy: y, t0: now(), lt: now(), moved: false, dragging: false, consumed: false, timer: null };
  if (!inputAllowed()) { g.consumed = true; return; }
  g.timer = setTimeout(() => {
    if (!g || g.kind !== 'one' || g.moved) return;
    buzz();
    if (prefs.mode === 'touch') { g.consumed = true; click(2, { x: g.sx, y: g.sy }); }   // long-press = right click
    else { g.dragging = true; sendNow({ t: 'down', b: 0 }); }                          // hold = start drag
  }, LONG_PRESS_MS);
}

function moveOne(p, prev, t) {
  if (!g.moved && Math.hypot(p.x - g.sx, p.y - g.sy) > TAP_SLOP) {
    g.moved = true;
    clearTimeout(g.timer);
  }
  if (g.consumed) return;
  if (prefs.mode === 'touch') {
    if (!g.moved) return;
    if (!g.dragging) {
      g.dragging = true;
      sendNow({ t: 'down', b: 0, ...toNorm(g.sx, g.sy) });
    }
    queueMove(toNorm(p.x, p.y));
  } else {
    const dx = p.x - prev.x, dy = p.y - prev.y;
    const dt = Math.max(t - g.lt, 1);
    g.lt = t;
    const speed = Math.hypot(dx, dy) / dt;                    // px per ms
    const f = prefs.speed * (1 + Math.min(speed, 2.5) * 0.9);  // pointer acceleration
    queueRel(dx * f, dy * f);
  }
}

function endOne(cancelled) {
  clearTimeout(g.timer);
  const p = [...pts.values()][0] || { x: g.sx, y: g.sy };
  if (g.dragging) {
    sendNow({ t: 'up', b: 0, ...(prefs.mode === 'touch' ? toNorm(p.x, p.y) : {}) });
  } else if (!g.consumed && !g.moved && !cancelled) {
    if (prefs.mode === 'touch') {
      let at = { x: g.sx, y: g.sy };
      // keep double-taps on the same spot so Windows sees a double-click
      if (lastTap && now() - lastTap.t < 450 && Math.hypot(at.x - lastTap.x, at.y - lastTap.y) < 24) at = { x: lastTap.x, y: lastTap.y };
      click(0, at);
      lastTap = { ...at, t: now() };
    } else if (now() - g.t0 < 300) {
      click(0);
    }
  }
  g = null;
}

// ----- two fingers
function startTwo() {
  if (g?.kind === 'one') {
    clearTimeout(g.timer);
    if (g.dragging) sendNow({ t: 'up', b: 0 });
  }
  const c = centroid();
  g = { kind: 'two', t0: now(), c0: c, last: c, mode: null, done: false };
}

function moveTwo() {
  const c = centroid();
  if (!g.mode) {
    if (Math.abs(c.d - g.c0.d) > 30) g.mode = 'pinch';
    else if (Math.hypot(c.x - g.c0.x, c.y - g.c0.y) > 12) g.mode = inputAllowed() ? 'scroll' : 'pinch';
  }
  if (g.mode === 'scroll') {
    queueWheel(-(c.x - g.last.x) * WHEEL_PER_PX, (c.y - g.last.y) * WHEEL_PER_PX);
  } else if (g.mode === 'pinch') {
    if (g.last.d > 0) zoomAt(c.x, c.y, c.d / g.last.d);
    panBy(c.x - g.last.x, c.y - g.last.y);
  }
  g.last = c;
}

function endTwo() {
  if (!g.done && !g.mode && now() - g.t0 < 300 && inputAllowed()) {
    click(2, prefs.mode === 'touch' ? { x: g.last.x, y: g.last.y } : null);   // two-finger tap = right click
  }
  g.done = true;
}

$('#zoomReset').addEventListener('click', resetView);

// ======================================================================== keyboard

const kbd = $('#kbd');
const keybar = $('#keybar');
const SENTINEL = '​​';       // lets Backspace fire even when "empty"
const NAV_KEYS = new Set(['Escape', 'Tab', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Delete', 'Insert',
  'Home', 'End', 'PageUp', 'PageDown', 'ContextMenu', 'PrintScreen', 'CapsLock',
  ...Array.from({ length: 12 }, (_, i) => `F${i + 1}`)]);
const EDIT_KEYS = new Set(['Backspace', 'Enter']);
const MOD_KEYS = new Set(['Control', 'Shift', 'Alt', 'Meta', 'AltGraph', 'OS']);
const sticky = new Set();
let lastValue = SENTINEL;
let composing = false;

function resetKbd() { kbd.value = SENTINEL; lastValue = SENTINEL; kbd.setSelectionRange(SENTINEL.length, SENTINEL.length); }

function sendKey(k, mods = []) { if (inputAllowed()) sendNow({ t: 'key', k, mods }); }
function sendText(s) { if (inputAllowed() && s) sendNow({ t: 'text', s }); }

function modsFrom(e) {
  const mods = new Set(sticky);
  if (e) { if (e.ctrlKey) mods.add('ctrl'); if (e.altKey) mods.add('alt'); if (e.shiftKey) mods.add('shift'); if (e.metaKey) mods.add('meta'); }
  return [...mods];
}

function clearSticky() {
  sticky.clear();
  keybar.querySelectorAll('[data-mod]').forEach((b) => b.classList.remove('on'));
}

function onKeyDown(e, inTextarea) {
  if (e.isComposing || e.keyCode === 229 || MOD_KEYS.has(e.key)) return;
  const mods = modsFrom(e);
  const combo = mods.some((m) => m !== 'shift');
  if (combo || NAV_KEYS.has(e.key) || (!inTextarea && EDIT_KEYS.has(e.key)) || (mods.length && e.key.length > 1)) {
    e.preventDefault();
    sendKey(e.key, mods);
    clearSticky();
  } else if (!inTextarea && e.key.length === 1) {
    e.preventDefault();
    sendText(e.key);
  }
  // otherwise: printable text / Backspace / Enter typed into the textarea arrives via 'input' below
}

kbd.addEventListener('keydown', (e) => onKeyDown(e, true));
kbd.addEventListener('compositionstart', () => { composing = true; });
kbd.addEventListener('compositionend', () => { composing = false; onKbdInput(); });
kbd.addEventListener('input', onKbdInput);

// Diff the textarea against its previous value. This works with every soft keyboard,
// including IME composition and autocorrect, which keydown alone cannot handle.
function onKbdInput() {
  const v = kbd.value;
  let i = 0;
  const n = Math.min(v.length, lastValue.length);
  while (i < n && v[i] === lastValue[i]) i++;
  const removed = lastValue.length - i;
  const inserted = v.slice(i);
  lastValue = v;
  for (let k = 0; k < removed; k++) sendKey('Backspace');
  if (inserted) {
    if (sticky.size && inserted.length === 1 && inserted !== '\n') { sendKey(inserted, modsFrom()); clearSticky(); }
    else sendText(inserted);
  }
  if (!composing && (v.length > 48 || !v.startsWith(SENTINEL) || v.includes('\n'))) resetKbd();
}

function placeKeybar() {
  const vv = window.visualViewport;
  const offset = vv ? Math.max(0, window.innerHeight - vv.height - vv.offsetTop) : 0;
  keybar.style.bottom = `${offset}px`;
}
window.visualViewport?.addEventListener('resize', placeKeybar);
window.visualViewport?.addEventListener('scroll', placeKeybar);

function openKeyboard() {
  resetKbd();
  kbd.focus({ preventScroll: true });
  keybar.hidden = false;
  placeKeybar();
}
function closeKeyboard() { kbd.blur(); keybar.hidden = true; clearSticky(); }

kbd.addEventListener('blur', () => setTimeout(() => {
  if (document.activeElement !== kbd && !keybar.matches(':active')) { keybar.hidden = true; clearSticky(); }
}, 150));

// keybar buttons must not steal focus from the textarea (that would close the soft keyboard)
keybar.addEventListener('pointerdown', (e) => { if (e.target.closest('button')) e.preventDefault(); });
keybar.addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  if (b.dataset.act === 'keyboard-close') { closeKeyboard(); return; }
  if (b.dataset.mod) {
    const m = b.dataset.mod;
    if (sticky.has(m)) sticky.delete(m); else sticky.add(m);
    b.classList.toggle('on', sticky.has(m));
  } else if (b.dataset.key) {
    sendKey(b.dataset.key, modsFrom());
    clearSticky();
  } else if (b.dataset.combo) {
    const parts = b.dataset.combo.split('+');
    sendKey(parts.pop(), parts);
  }
  buzz();
});

// hardware keyboards (tablets, desktop browsers) when the hidden textarea isn't focused
window.addEventListener('keydown', (e) => {
  if ($('#viewer').hidden || e.target === kbd || e.target.closest?.('input, select, textarea, .sheet')) return;
  onKeyDown(e, false);
});

// ======================================================================== toolbar & settings

function setMode(mode) {
  prefs.mode = mode;
  store.set('mode', mode);
  $('#modeLabel').textContent = mode === 'touch' ? 'Touch' : 'Trackpad';
  $('#iconTouch').hidden = mode !== 'touch';
  $('#iconPad').hidden = mode === 'touch';
  $('#modeSeg').querySelectorAll('button').forEach((b) => b.classList.toggle('on', b.dataset.mode === mode));
  $('#modeHelp').textContent = mode === 'touch' ? 'Tap exactly where you want to click' : 'Use the screen like a laptop touchpad';
}

function setPaused(p) {
  paused = p;
  wsSend({ t: p ? 'pause' : 'resume' });
  $('#iconPause').hidden = p;
  $('#iconPlay').hidden = !p;
  updateOverlay();
}

function setToolbar(visible) {
  prefs.toolbar = visible;
  store.set('toolbar', visible);
  $('#toolbar').hidden = !visible;
  $('#grip').hidden = visible;
}

function fillMonitors() {
  const sel = $('#monitorSel');
  sel.innerHTML = '';
  for (const m of monitors) sel.add(new Option(m.label, m.index, false, m.index === currentMonitor));
}

let toastTimer = null;
function toast(text) {
  const el = $('#toast');
  el.textContent = text;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 1600);
}

const fsSupported = !!(document.documentElement.requestFullscreen || document.documentElement.webkitRequestFullscreen);
$('#fsBtn').hidden = !fsSupported;

function toggleFullscreen() {
  const d = document;
  if (d.fullscreenElement || d.webkitFullscreenElement) (d.exitFullscreen || d.webkitExitFullscreen).call(d);
  else (d.documentElement.requestFullscreen || d.documentElement.webkitRequestFullscreen).call(d.documentElement, { navigationUI: 'hide' });
}

$('#toolbar').addEventListener('click', (e) => {
  const act = e.target.closest('button')?.dataset.act;
  if (act === 'keyboard') openKeyboard();
  else if (act === 'mode') { setMode(prefs.mode === 'touch' ? 'trackpad' : 'touch'); toast(prefs.mode === 'touch' ? 'Touch mode' : 'Trackpad mode'); }
  else if (act === 'pause') setPaused(!paused);
  else if (act === 'fullscreen') toggleFullscreen();
  else if (act === 'settings') $('#sheet').hidden = false;
  else if (act === 'hide') { setToolbar(false); toast('Tap the bar at the bottom to bring the toolbar back'); }
});
$('#grip').addEventListener('click', () => setToolbar(true));

$('#sheet').addEventListener('click', (e) => {
  const act = e.target.closest('button')?.dataset.act;
  if (e.target === $('#sheet') || act === 'sheet-close') $('#sheet').hidden = true;
  else if (act === 'forget') {
    if (!confirm('Forget this PC? You will need to pair again.')) return;
    store.del('token');
    clearTimeout(reconnectTimer);
    const sock = ws;
    ws = null;
    sock?.close();
    $('#sheet').hidden = true;
    showPair('');
  } else if (e.target.dataset.mode) setMode(e.target.dataset.mode);
});

$('#monitorSel').addEventListener('change', (e) => {
  const i = Number(e.target.value);
  currentMonitor = i;
  prefs.monitor = i;
  store.set('monitor', i);
  wsSend({ t: 'monitor', i });
});

$('#speed').addEventListener('input', (e) => { prefs.speed = Number(e.target.value); store.set('speed', prefs.speed); });
$('#statsToggle').addEventListener('change', (e) => {
  prefs.stats = e.target.checked;
  store.set('stats', prefs.stats);
  $('#hudDetail').hidden = true;
  renderStats();
});

// Stop the stream while the tab is in the background; resume when it's visible again.
document.addEventListener('visibilitychange', () => {
  if (paused) return;
  wsSend({ t: document.hidden ? 'pause' : 'resume' });
});

// ======================================================================== start

function startViewer() {
  $('#pair').hidden = true;
  $('#viewer').hidden = false;
  setMode(prefs.mode);
  setToolbar(prefs.toolbar);
  $('#speed').value = prefs.speed;
  $('#statsToggle').checked = prefs.stats;
  resize();
  renderStats();
  connect();
}

if (store.get('token')) startViewer();
else {
  showPair('');
  if (qrCode) pairWithCode(qrCode);
}
