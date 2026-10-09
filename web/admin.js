'use strict';
/* ScreenMirror host dashboard (only served to localhost). */

const $ = (s) => document.querySelector(s);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let ws = null;
let state = null;
let selectedIp = null;

function connect() {
  ws = new WebSocket(`ws://${location.host}/ws/admin`);
  ws.onopen = () => { $('#offline').hidden = true; };
  ws.onmessage = (e) => { state = JSON.parse(e.data); render(); };
  ws.onclose = () => {
    ws = null;
    $('#offline').hidden = false;
    $('#statusDot').className = 'dot';
    $('#statusText').textContent = 'Offline';
    setTimeout(connect, 1500);
  };
}

function command(cmd) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(cmd)); }
function set(key, value) { command({ cmd: 'set', key, value }); }

// ------------------------------------------------------------------ helpers
function ago(ts) {
  const s = Math.max(0, Date.now() / 1000 - ts);
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
}
function duration(ts) {
  const s = Math.max(0, Math.floor(Date.now() / 1000 - ts));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
  return h ? `${h}h ${m}m` : m ? `${m}m ${s % 60}s` : `${s}s`;
}
const cap = (s) => s[0].toUpperCase() + s.slice(1);
const ms = (v) => (v == null ? '—' : `${v} ms`);

/** Keep one DOM node per item (keyed by id) so buttons survive the 1-second refresh. */
function sync(container, items, create, update) {
  const existing = new Map([...container.children].map((el) => [el.dataset.id, el]));
  items.forEach((item, i) => {
    let el = existing.get(item.id);
    if (!el) { el = create(item); el.dataset.id = item.id; }
    existing.delete(item.id);
    update(el, item);
    if (container.children[i] !== el) container.insertBefore(el, container.children[i] || null);
  });
  existing.forEach((el) => el.remove());
}

function el(html) {
  const t = document.createElement('template');
  t.innerHTML = html.trim();
  return t.content.firstElementChild;
}

const PHONE_ICON = '<svg class="i" viewBox="0 0 24 24"><rect x="6" y="2" width="12" height="20" rx="2.5"/><path d="M11 18h2"/></svg>';

// ------------------------------------------------------------------ render
function render() {
  const s = state;
  const streaming = s.settings.streaming;
  document.title = `ScreenMirror · ${s.clients.length} connected`;
  $('#host').textContent = s.host;
  $('#statusDot').className = streaming ? 'dot live' : 'dot';
  $('#statusText').textContent = streaming
    ? (s.clients.length ? `Streaming to ${s.clients.length} device${s.clients.length > 1 ? 's' : ''}` : 'Ready')
    : 'Streaming stopped';
  const btn = $('#streamBtn');
  btn.textContent = streaming ? 'Stop streaming' : 'Start streaming';
  btn.className = streaming ? 'btn danger' : 'btn primary';

  // pairing
  if (!selectedIp || !s.ips.includes(selectedIp)) selectedIp = s.ips[0];
  const url = `http://${selectedIp}:${s.port}/`;
  $('#url').textContent = url;
  $('#code').textContent = s.code;
  const qrSrc = `/api/qr.svg?ip=${encodeURIComponent(selectedIp)}&c=${s.code}`;
  if ($('#qr').getAttribute('src') !== qrSrc) $('#qr').src = qrSrc;
  $('#ipField').hidden = s.ips.length < 2;
  const ipSel = $('#ipSel');
  if (ipSel.options.length !== s.ips.length) {
    ipSel.innerHTML = s.ips.map((ip) => `<option>${esc(ip)}</option>`).join('');
  }
  ipSel.value = selectedIp;
  $('#altUrl').textContent = `Also reachable as ${s.hostUrl} on most networks.`;

  // requests
  $('#requestsCard').hidden = !s.requests.length;
  sync($('#requests'), s.requests,
    (r) => el(`<div class="request"><div class="who"><b></b><small></small></div>
      <button class="btn sm" data-cmd="deny">Deny</button><button class="btn sm primary" data-cmd="approve">Approve</button></div>`),
    (node, r) => {
      node.querySelector('b').textContent = r.name;
      node.querySelector('small').textContent = `${r.ip} · wants to connect`;
    });

  // settings (don't fight the user while they drag)
  $('#fpsSeg').querySelectorAll('button').forEach((b) => b.classList.toggle('on', Number(b.dataset.v) === s.settings.fps));
  if (document.activeElement !== $('#quality')) $('#quality').value = s.settings.quality;
  $('#qualityVal').textContent = $('#quality').value;
  if (document.activeElement !== $('#maxWidth')) $('#maxWidth').value = String(s.settings.max_width);
  $('#allowControl').checked = s.settings.allow_control;
  $('#encode').textContent = s.clients.length ? `Encode ${s.encodeMs} ms` : 'Idle';

  // connected clients
  const monitorLabel = (i) => (s.monitors.find((m) => m.index === i)?.label || `Screen ${i}`).split('  ·')[0];
  $('#clientCount').textContent = s.clients.length;
  $('#clientsEmpty').hidden = s.clients.length > 0;
  sync($('#clients'), s.clients,
    () => el(`<div class="device">
      <div class="device-top">
        <span class="device-icon">${PHONE_ICON}</span>
        <div class="device-name"><b class="n"></b><small class="sub"></small></div>
        <span class="pill q"><span class="dot"></span><span class="ql"></span></span>
      </div>
      <div class="metrics">
        <div class="metric"><small>FPS</small><b class="fps"></b></div>
        <div class="metric"><small>Latency</small><b class="lat"></b></div>
        <div class="metric"><small>Ping</small><b class="rtt"></b></div>
        <div class="metric"><small>Bitrate</small><b class="rate"></b></div>
      </div>
      <div class="device-actions">
        <label class="switch"><input type="checkbox" data-cmd="device_control"><span></span></label><span>Allow control</span>
        <span class="spacer"></span>
        <button class="btn sm danger" data-cmd="kick">Disconnect</button>
      </div></div>`),
    (node, c) => {
      node.querySelector('.n').textContent = c.name;
      node.querySelector('.sub').textContent = `${c.ip} · ${monitorLabel(c.monitor)} · ${c.paused ? 'paused' : 'connected ' + duration(c.since)}`;
      node.querySelector('.q').className = `pill q q-${c.quality}`;
      node.querySelector('.ql').textContent = c.paused ? 'Paused' : cap(c.quality);
      node.querySelector('.fps').textContent = c.deviceFps ?? c.fps;
      // server-side latency includes the ack's trip back; show the capture-to-screen estimate
      node.querySelector('.lat').textContent = c.latency == null ? '—' : ms(Math.max(1, Math.round(c.latency - (c.rtt || 0) / 2)));
      node.querySelector('.rtt').textContent = ms(c.rtt);
      node.querySelector('.rate').textContent = `${(c.kbps / 1000).toFixed(1)} Mb/s`;
      const dev = s.devices.find((d) => d.id === c.deviceId);
      const toggle = node.querySelector('[data-cmd="device_control"]');
      toggle.checked = dev ? dev.allowControl : false;
      toggle.disabled = !s.settings.allow_control;
      toggle.dataset.device = c.deviceId;
    });

  // paired devices
  $('#devicesEmpty').hidden = s.devices.length > 0;
  sync($('#devices'), s.devices,
    () => el(`<div class="paired"><span class="device-icon">${PHONE_ICON}</span>
      <div class="device-name"><b></b><small></small></div>
      <button class="btn sm danger" data-cmd="revoke">Remove</button></div>`),
    (node, d) => {
      node.querySelector('b').textContent = d.name;
      node.querySelector('small').textContent = d.online ? 'Online now' : `Last seen ${ago(d.lastSeen)}`;
    });
}

// ------------------------------------------------------------------ events
document.addEventListener('click', (e) => {
  const b = e.target.closest('[data-cmd]');
  if (!b || b.type === 'checkbox') return;
  const id = b.closest('[data-id]')?.dataset.id;
  const cmd = b.dataset.cmd;
  if (cmd === 'approve' || cmd === 'deny') command({ cmd, request: id });
  else if (cmd === 'kick') command({ cmd, client: id });
  else if (cmd === 'revoke') {
    const name = state.devices.find((d) => d.id === id)?.name || 'this device';
    if (confirm(`Remove ${name}? It will need to pair again.`)) command({ cmd, device: id });
  }
});
document.addEventListener('change', (e) => {
  if (e.target.dataset.cmd === 'device_control') command({ cmd: 'device_control', device: e.target.dataset.device, value: e.target.checked });
});

$('#streamBtn').addEventListener('click', () => state && set('streaming', !state.settings.streaming));
$('#regen').addEventListener('click', () => command({ cmd: 'regen_code' }));
$('#fpsSeg').addEventListener('click', (e) => { if (e.target.dataset.v) set('fps', Number(e.target.dataset.v)); });
$('#quality').addEventListener('input', (e) => { $('#qualityVal').textContent = e.target.value; });
$('#quality').addEventListener('change', (e) => set('quality', Number(e.target.value)));
$('#maxWidth').addEventListener('change', (e) => set('max_width', Number(e.target.value)));
$('#allowControl').addEventListener('change', (e) => set('allow_control', e.target.checked));
$('#ipSel').addEventListener('change', (e) => { selectedIp = e.target.value; render(); });
$('#copy').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText($('#url').textContent); $('#copy').textContent = 'Copied'; }
  catch { $('#copy').textContent = 'Copy failed'; }
  setTimeout(() => { $('#copy').textContent = 'Copy'; }, 1500);
});

connect();
