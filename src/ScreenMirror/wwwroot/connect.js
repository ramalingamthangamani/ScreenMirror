/* ScreenMirror connection page (shown on the PC): link, QR code, pairing code and paired devices. */
(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  let urlIndex = 0;
  let qrKey = '';

  function cell(text, cls) {
    const td = document.createElement('td');
    if (cls) {
      const dot = document.createElement('span');
      dot.className = cls;
      td.appendChild(dot);
    }
    td.appendChild(document.createTextNode(text));
    return td;
  }

  function render(info) {
    $('host').textContent = `${info.host} · port ${info.port}`;
    const code = info.code || '';
    $('code').textContent = code ? `${code.slice(0, 3)} ${code.slice(3)}` : '------';
    if (urlIndex >= info.urls.length) urlIndex = 0;
    $('url').textContent = info.urls[urlIndex];

    const list = $('urls');
    list.textContent = '';
    info.urls.forEach((u, i) => {
      if (i === urlIndex) return;
      const li = document.createElement('li');
      const b = document.createElement('button');
      b.textContent = u;
      b.title = 'Use this address for the QR code';
      b.onclick = () => { urlIndex = i; refresh(); };
      li.append('Also: ', b);
      list.appendChild(li);
    });

    const key = `${urlIndex}|${code}|${info.urls[urlIndex]}`;
    if (key !== qrKey) {
      qrKey = key;
      $('qr').src = `qr.svg?i=${urlIndex}&c=${encodeURIComponent(code)}`;
    }

    const parts = [
      `Streaming: <b>${info.hostStreaming ? 'on' : 'paused'}</b>`,
      `Remote control: <b>${info.inputEnabled ? 'allowed' : 'blocked'}</b>`,
    ];
    $('status').innerHTML = parts.join('');

    const body = $('devices');
    body.textContent = '';
    const devices = info.devices || [];
    if (!devices.length) {
      const tr = document.createElement('tr');
      const td = cell('No paired devices yet.');
      td.colSpan = 4;
      td.className = 'empty';
      tr.appendChild(td);
      body.appendChild(tr);
    }
    for (const d of devices) {
      const tr = document.createElement('tr');
      tr.appendChild(cell(d.name));
      tr.appendChild(cell(d.connected ? `connected (${d.address})` : `last seen ${new Date(d.lastSeen).toLocaleString()}`, d.connected ? 'dot on' : 'dot'));
      tr.appendChild(cell(d.connected && d.streaming ? `${d.fps} fps · ${d.latency} ms · ${d.kbps} kbps` : '-'));
      tr.appendChild(cell(d.control ? 'allowed' : 'view only'));
      body.appendChild(tr);
    }
  }

  async function refresh() {
    try {
      const res = await fetch('api/info', { cache: 'no-store' });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      render(await res.json());
    } catch (e) {
      $('status').innerHTML = '<span class="err">ScreenMirror is not running.</span>';
    }
  }

  refresh();
  setInterval(refresh, 2000);
})();
