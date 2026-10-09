# ScreenMirror

Stream a Windows PC's screen to phones and tablets on the same local network, and control the PC
back from the device (touch, trackpad, keyboard). The device only needs a web browser; nothing is
installed on it.

There are two independent host implementations in this repo:

| | Python (`server.py`) | C# / .NET 10 (`src/ScreenMirror`) |
|---|---|---|
| Runtime | Python 3.9+ (venv created by `run.bat`) | .NET 10 Desktop Runtime, single exe |
| Default port | 8765 | 8642 (tries the next 9 ports if busy, unless `--port` is given) |
| Web UI | `web/` folder | embedded `wwwroot/` (override with `--webroot`) |
| Pairing | 6-digit code / QR, or approve-on-PC access requests | 6-digit code / QR link (no access requests) |
| PC-side UI | Admin dashboard `http://localhost:<port>/admin` | Connection page `http://localhost:<port>/connect` + notification-area (tray) menu |
| Data / settings | `data/` next to `server.py` | `%APPDATA%\ScreenMirror` (`devices.json`, `screenmirror.log`, `port.txt`) |
| Tests | - | `dotnet test` (xUnit, `tests/`) |

Both hosts are Windows only.

## Features

- Live JPEG stream of the desktop over WebSocket, with flow control (at most 2 un-acknowledged
  frames per device) so slow devices don't build up lag.
- Multi-monitor: each device picks which screen to view.
- Remote input: mouse move/click/drag, right-click, scroll, text typing and key combos
  (on-screen bar with Ctrl/Alt/Shift/Win, Esc, Tab, arrows, Copy/Paste/Undo, Alt+Tab, F5 ...).
- Two input modes on the device: **Touch** (tap = click, long-press = right-click,
  drag = click-drag, two-finger swipe = scroll, pinch = zoom the view) and **Trackpad**
  (swipe moves the pointer, tap = click, two-finger tap = right-click, hold then move = drag).
- Per-device pause, fullscreen, optional FPS/latency overlay.
- Python host: pairing codes / QR, approve-on-PC access requests, admin dashboard with live
  per-device stats (fps, kbps, RTT, latency, connection quality), kick, revoke, per-device
  control permission.
- C# host: pairing codes / QR link, connection page with live device table, tray menu to pause
  streaming, block remote control globally or per device, regenerate the code and revoke devices;
  per-device quality preset (Low 960 px / Balanced 1280 px / High 1920 px, by long side)
  with a sharper "refine" frame once the screen goes still; frames shared between devices
  watching the same monitor at the same preset. Requests from other web sites are refused
  (Host and Origin checks) so a page open elsewhere in a LAN browser cannot drive the PC.

## Quick start

### Python

```bat
run.bat
```

The first run creates `.venv` and installs `requirements.txt` (aiohttp, mss, Pillow, qrcode).
The console prints the dashboard URL, the phone URL(s) and the current pairing code, and the
dashboard opens in your browser. Extra arguments are passed through, e.g. `run.bat --port 9000`.

Manual equivalent:

```bat
python -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
.venv\Scripts\python server.py
```

### C# (.NET 10)

Requires the .NET 10 SDK to build.

```bat
build.bat
dist\ScreenMirror.exe
```

`build.bat` empties `dist\` and runs `dotnet publish src\ScreenMirror -c Release -o dist`
(pass `--no-pause` to skip the pause on failure, e.g. in CI). The project file pins a single-file,
framework-dependent win-x64 publish (a few hundred KB), so the target PC needs the .NET 10 Desktop
Runtime and ASP.NET Core Runtime. Note: with the .NET 10 SDK the `--self-contained false` switch is
ignored and yields a 130 MB self-contained exe; use `-p:SelfContained=false` (as the project does). For development: `dotnet run --project src\ScreenMirror`.

The exe is a windowed app (no console window): it puts an icon in the notification area and
opens the connection page (link, QR code, pairing code) in your browser. Launching it a second
time just reopens that page. Run it with `--no-tray` from a terminal to see console output and
stop it with Ctrl+C. Startup errors are shown in a message box and written to the log.

To run the unit tests (pairing store, option parsing, host/origin checks, QR encoder, embedded
assets, protocol helpers):

```bat
dotnet test
```

`ScreenMirror.slnx` at the repo root holds both the app and `tests\ScreenMirror.Tests`.

## Command-line options

### Python (`server.py` / `run.bat`)

| Option | Default | Description |
|---|---|---|
| `--port N` | 8765 | TCP port to listen on (all interfaces) |
| `--no-browser` | off | Don't open the admin dashboard on start |

### C# (`ScreenMirror.exe`)

| Option | Default | Description |
|---|---|---|
| `--port N`, `-p N` | 8642 | Port to listen on. If omitted and 8642 is busy, 8643-8651 are tried |
| `--no-tray`, `--headless` | off | Run attached to the parent console; Ctrl+C to quit |
| `--no-browser` | off | Don't open the connection page at startup |
| `--data-dir PATH` | `%APPDATA%\ScreenMirror` | Where `devices.json` (paired devices), the log and `port.txt` are kept. One instance runs per data directory |
| `--webroot PATH` | embedded | Serve web files from a folder instead of the embedded copy (UI development) |
| `--verbose`, `-v` | off | Verbose logging |

Unknown arguments print the usage line and exit with code 2.

## Connecting a phone or tablet

The phone must be on the same network as the PC. Open `http://<pc-ip>:<port>/` in its browser
(the Python host prints every LAN address at startup; the dashboard also shows a `<hostname>.local` URL).

With either host the device has to be paired once; it then stores a token and reconnects
automatically.

**Python host** - two ways:

1. **Pairing code / QR**: the dashboard shows a 6-digit code and a QR code. Scanning the QR
   opens `http://<ip>:<port>/?code=<code>` with the code filled in; otherwise type the code
   and a device name and tap **Pair**. A code works for one device and is then replaced.
   After 5 wrong attempts the code is rotated. You can also generate a new code manually.
2. **Ask the PC for access**: the device sends a request that appears under
   *Access requests* on the dashboard; click approve or deny. Requests expire after 3 minutes
   (max 10 pending).

On the device, **Forget this PC** (Settings sheet) removes its stored pairing.

**C# host** - pairing code / QR only. The connection page (`http://localhost:<port>/connect`,
opened at startup and from the tray menu) shows the address, a QR code and the 6-digit code; the
tray menu also shows the code and copies a direct pairing link (`http://<ip>:<port>/?code=<code>`)
to the clipboard. Opening the page on the device shows a code prompt (pre-filled and submitted
automatically when the link or QR carried the code). A code pairs one device and is then
replaced; after 5 wrong attempts the code is replaced and pairing pauses for 30 seconds.
*New pairing code* in the tray menu replaces it manually. **Forget this PC** in the device's
Settings sheet removes its pairing on both sides.

## Admin dashboard (Python host)

`http://localhost:<port>/admin` - reachable **only from the PC itself** (requests from other
addresses get 403). It shows:

- Connect a device: URL (with a network-address picker if the PC has several), copy button,
  QR code and pairing code (with regenerate).
- Access requests: approve / deny.
- Stream settings (below).
- Connected devices: monitor, fps, bandwidth, RTT, latency, quality rating; kick.
- Paired devices: last seen, per-device remote-control toggle, revoke (disconnects it and
  requires re-pairing).
- A global start/stop streaming button.

## Connection page and tray menu (C# host)

`http://localhost:<port>/connect` - reachable **only from the PC itself** (other addresses get
403). It shows the pairing code, the QR code (with a picker if the PC has several addresses),
whether streaming / remote control is on, and a table of paired devices with connection state,
fps, latency and bandwidth. It refreshes every 2 seconds.

The tray icon's menu (right-click; double-click opens the connection page) offers:

- Open in browser / Show pairing code (copies the pairing link) / New pairing code.
- Pause streaming (all devices see a paused message until resumed).
- Allow remote control (global switch; per-device *Allow control* under *Paired devices*).
- Paired devices: per device, allow or block control and *Remove device* (disconnects it and
  requires re-pairing); *Revoke all devices*.
- Open data folder, Quit.

A balloon tip appears when a device connects.

## Settings (Python host)

Changed from the dashboard and saved to `data/settings.json`:

| Setting | Values | Default | Meaning |
|---|---|---|---|
| `fps` | 5-60 (dashboard: 15 / 30 / 60) | 30 | Frame-rate cap; actual rate adapts to the network |
| `quality` | 30-95 | 70 | JPEG quality |
| `max_width` | 0 (native), 1280, 1600, 1920, 2560 | 1920 | Maximum stream width in pixels |
| `allow_control` | true / false | true | Global switch for remote input; per-device permission also applies |
| `streaming` | true / false | true | Global stream on/off |

Paired devices are stored in `data/devices.json` (only a SHA-256 hash of each token is kept).
Delete the `data/` folder to reset everything.

The C# host persists only its paired devices (`%APPDATA%\ScreenMirror\devices.json`, token
hashes and per-device control permission). Frame rate is capped at 30 fps and each device
chooses its quality preset in its own settings; pause and the global control switch live in
the tray menu and reset on restart. Delete the data folder to reset pairing.

## Windows Firewall

The first time the host listens, Windows may ask whether to allow it; allow it on **Private**
networks. If phones can't connect, add an inbound rule for the port (elevated prompt):

```bat
netsh advfirewall firewall add rule name="ScreenMirror" dir=in action=allow protocol=TCP localport=8765 profile=private
```

Use `8642` for the C# host (or whatever `--port` you chose). Make sure the Wi-Fi network is set
to *Private* in Windows settings, or change `profile=` accordingly.

## Security notes

- Intended for a trusted home/office LAN only. Traffic is plain HTTP/WebSocket (no TLS);
  do not port-forward it to the internet.
- Anyone who can view the stream sees your screen, and a device with control can do anything
  you can with mouse and keyboard.
- Python host: devices must pair (one-time 6-digit code, rotated on use and after 5 failures)
  or be approved on the PC; the dashboard and its APIs are localhost-only. Revoke devices you
  no longer use and turn off *Remote control* if you only want viewing.
- C# host: devices must pair with the one-time 6-digit code (replaced on use and after 5
  failures, with a 30 s pause). The connection page and QR endpoint are localhost-only.
  Requests whose Host header is not this PC's IP, name or `localhost` get 421 (DNS rebinding
  guard) and WebSocket / pairing requests from another web origin get 403. Remove devices you
  no longer use from the tray menu and turn off *Allow remote control* if you only want viewing.

## Troubleshooting

- **Phone can't load the page**: check both are on the same network (not a guest/isolated
  Wi-Fi), try each IP the host prints, and check the firewall rule above. VPN software can
  also block LAN access.
- **"Could not listen on port"**: another instance or program uses the port. Close it or pass
  `--port`. (The C# host only auto-tries other ports when `--port` is not given.)
- **`run.bat` says Python is required**: install Python 3.9+ from python.org and make sure
  `python` or `py` is on PATH. If dependency install failed, delete `.venv` and run again.
- **Device keeps asking to pair**: it was revoked on the PC or `devices.json` was reset; pair
  again with the current code.
- **"Too many wrong codes"** (C#): wait 30 seconds and use the new code shown on the PC.
- **Pairing code / connection page not shown** (C#): it is only served to the PC itself; open
  `http://localhost:<port>/connect` on the PC, or use the tray menu.
- **Clicks can't reach some windows**: Windows blocks input injection into elevated
  (administrator) windows from a non-elevated process. Run the host as administrator if you
  need to control those.
- **Laggy stream**: lower `quality` / `max_width` / `fps` (Python) or pick the Low preset (C#);
  prefer 5 GHz Wi-Fi.
- **C# logs**: `%APPDATA%\ScreenMirror\screenmirror.log` (or your `--data-dir`); use `--verbose`
  for more detail.
