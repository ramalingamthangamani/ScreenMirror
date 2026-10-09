"""ScreenMirror - stream this PC's screen to phones/tablets on the LAN and accept
mouse / touch / keyboard input back.

    python server.py [--port 8765] [--no-browser]

Phones open http://<pc-ip>:<port>/ ; the host dashboard is http://localhost:<port>/admin
"""
import argparse
import asyncio
import collections
import hashlib
import json
import secrets
import socket
import struct
import sys
import time
import webbrowser
from pathlib import Path

import qrcode
import qrcode.image.svg
from aiohttp import WSMsgType, web

import inputctl
from capture import Capturer

ROOT = Path(__file__).resolve().parent
WEB = ROOT / "web"
DATA = ROOT / "data"

DEFAULT_SETTINGS = {"streaming": True, "allow_control": True, "fps": 30, "quality": 70, "max_width": 1920}
SETTING_RULES = {
    "streaming": lambda v: isinstance(v, bool),
    "allow_control": lambda v: isinstance(v, bool),
    "fps": lambda v: _is_int(v) and 5 <= v <= 60,
    "quality": lambda v: _is_int(v) and 30 <= v <= 95,
    "max_width": lambda v: _is_int(v) and v in (0, 1280, 1600, 1920, 2560),
}
FRAME_HEADER = struct.Struct("<IdHH")   # frame id, capture time (ms since epoch), width, height
MAX_IN_FLIGHT = 2                       # un-acked frames per client before we wait (flow control)
MAX_FAILED_PAIRINGS = 5


def _is_int(v):
    return isinstance(v, int) and not isinstance(v, bool)


def sha256(text):
    return hashlib.sha256(text.encode()).hexdigest()


def new_code():
    return f"{secrets.randbelow(10**6):06d}"


def lan_ips():
    ips = []
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("10.255.255.255", 1))  # no packet is sent; picks the default-route interface
            ips.append(s.getsockname()[0])
    except OSError:
        pass
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ip = info[4][0]
            if ip not in ips and not ip.startswith("127."):
                ips.append(ip)
    except OSError:
        pass
    return ips or ["127.0.0.1"]


def connection_quality(rtt, latency):
    if rtt is None or latency is None:
        return "unknown"
    if rtt < 25 and latency < 100:
        return "excellent"
    if rtt < 60 and latency < 180:
        return "good"
    if rtt < 150 and latency < 350:
        return "fair"
    return "poor"


def clean_name(name):
    name = " ".join(str(name or "").split())[:40]
    return name or "Device"


def is_local(request):
    return request.remote in ("127.0.0.1", "::1")


# --------------------------------------------------------------------------- persistence

class Store:
    def __init__(self):
        DATA.mkdir(exist_ok=True)
        saved = self._load("settings.json")
        self.settings = {k: saved[k] if k in saved and SETTING_RULES[k](saved[k]) else v
                         for k, v in DEFAULT_SETTINGS.items()}
        devices = self._load("devices.json")  # id -> {name, token_hash, paired_at, last_seen, allow_control}
        self.devices = {i: d for i, d in devices.items()
                        if isinstance(d, dict) and isinstance(d.get("token_hash"), str)}
        for d in self.devices.values():
            d["name"] = clean_name(d.get("name"))
            d.setdefault("paired_at", time.time())
            d.setdefault("last_seen", d["paired_at"])

    @staticmethod
    def _load(name):
        try:
            value = json.loads((DATA / name).read_text("utf-8"))
            return value if isinstance(value, dict) else {}
        except (OSError, ValueError):
            return {}

    @staticmethod
    def _save(name, value):
        tmp = DATA / (name + ".tmp")
        tmp.write_text(json.dumps(value, indent=2), "utf-8")
        tmp.replace(DATA / name)

    def save_settings(self):
        self._save("settings.json", self.settings)

    def save_devices(self):
        self._save("devices.json", self.devices)

    def add_device(self, name):
        device_id, token = secrets.token_hex(6), secrets.token_urlsafe(32)
        now = time.time()
        self.devices[device_id] = dict(name=clean_name(name), token_hash=sha256(token),
                                       paired_at=now, last_seen=now, allow_control=True)
        self.save_devices()
        return device_id, token

    def device_for_token(self, token):
        if not isinstance(token, str):
            return None
        digest = sha256(token)
        for device_id, dev in self.devices.items():
            if secrets.compare_digest(dev["token_hash"], digest):
                return device_id
        return None


# --------------------------------------------------------------------------- connected phone/tablet

class Client:
    def __init__(self, ws, device_id, ip, monitor):
        self.id = secrets.token_hex(4)
        self.ws, self.device_id, self.ip = ws, device_id, ip
        self.monitor = monitor
        self.paused = False
        self.wake = asyncio.Event()
        self.outbox = collections.deque()
        self.in_flight = {}              # frame id -> (send time, capture time), epoch ms
        self.last_frame_id = 0
        self.cursor = None               # last cursor message sent
        self.buttons = set()             # mouse buttons this client is holding down
        self.connected_at = time.time()
        # stats
        self.window_frames = self.window_bytes = 0
        self.fps = self.kbps = 0
        self.latency = None              # capture -> displayed on device -> ack (ms, EMA)
        self.rtt = None                  # reported by device (ping/pong)
        self.device_fps = None

    def send(self, msg):
        self.outbox.append(json.dumps(msg, separators=(",", ":")))
        self.wake.set()


# --------------------------------------------------------------------------- hub

class Hub:
    def __init__(self, store, port):
        self.store, self.port = store, port
        self.settings = store.settings
        self.host = socket.gethostname()
        self.ips = lan_ips()
        self.clients = {}
        self.admins = set()
        self.latest = {}                 # monitor index -> latest Frame
        self.cursor = (0, 0)
        self.pairing_code = new_code()
        self.failed_pairings = 0
        self.requests = {}               # pending "approve on PC" pairing requests
        self.loop = None
        self.admin_dirty = asyncio.Event()
        self.capturer = Capturer(
            self.settings,
            on_frame=lambda f: self.loop.call_soon_threadsafe(self.on_frame, f),
            on_cursor=lambda x, y: self.loop.call_soon_threadsafe(self.on_cursor, x, y),
            on_monitors=lambda m: self.loop.call_soon_threadsafe(self.on_monitors, m),
        )

    # ----- capture plumbing
    @property
    def monitors(self):
        return self.capturer.monitors

    def default_monitor(self):
        mons = self.monitors
        for m in mons[1:]:
            if m["left"] == 0 and m["top"] == 0:
                return m["index"]
        return 1 if len(mons) > 1 else 0

    def monitor_list(self):
        mons = self.monitors
        out = [dict(index=m["index"], width=m["width"], height=m["height"],
                    label=f"Screen {m['index']}  ·  {m['width']}×{m['height']}") for m in mons[1:]]
        if len(mons) > 2:
            out.append(dict(index=0, width=mons[0]["width"], height=mons[0]["height"],
                            label=f"All screens  ·  {mons[0]['width']}×{mons[0]['height']}"))
        return out

    def update_wanted(self):
        if self.settings["streaming"]:
            wanted = frozenset(c.monitor for c in self.clients.values() if not c.paused)
        else:
            wanted = frozenset()
        for m in wanted - self.capturer.wanted:
            self.capturer.refresh(m)
        self.capturer.wanted = wanted

    def on_frame(self, frame):
        self.latest[frame.monitor] = frame
        for c in self.clients.values():
            if c.monitor == frame.monitor:
                c.wake.set()

    def on_cursor(self, x, y):
        self.cursor = (x, y)
        for c in self.clients.values():
            self.push_cursor(c)

    def push_cursor(self, c):
        if c.monitor >= len(self.monitors):
            return
        m = self.monitors[c.monitor]
        nx = (self.cursor[0] - m["left"]) / m["width"]
        ny = (self.cursor[1] - m["top"]) / m["height"]
        msg = {"t": "cur", "x": round(nx, 5), "y": round(ny, 5), "v": 0 <= nx < 1 and 0 <= ny < 1}
        if msg != c.cursor:
            c.cursor = msg
            c.send(msg)

    def on_monitors(self, monitors):
        for c in self.clients.values():
            if c.monitor >= len(monitors):
                c.monitor = self.default_monitor()
            c.send({"t": "monitors", "monitors": self.monitor_list(), "monitor": c.monitor})
        self.update_wanted()
        self.admin_dirty.set()

    # ----- permissions
    def can_control(self, c):
        dev = self.store.devices.get(c.device_id, {})
        return bool(self.settings["streaming"] and self.settings["allow_control"]
                    and dev.get("allow_control", True))

    def push_state(self, c):
        c.send({"t": "state", "streaming": self.settings["streaming"], "control": self.can_control(c)})

    # ----- per-client sender (the only task that writes to a client's socket)
    async def sender(self, c):
        try:
            await self._send_loop(c)
        except (ConnectionError, RuntimeError):
            pass  # socket went away; the receive loop cleans up

    async def _send_loop(self, c):
        while not c.ws.closed:
            await c.wake.wait()
            c.wake.clear()
            while c.outbox:
                await c.ws.send_str(c.outbox.popleft())
            if c.paused or not self.settings["streaming"]:
                continue
            frame = self.latest.get(c.monitor)
            if frame is None or frame.id <= c.last_frame_id:
                continue
            now = time.time() * 1000
            for fid in [f for f, (sent, _) in c.in_flight.items() if now - sent > 3000]:
                del c.in_flight[fid]     # device stopped acking (backgrounded tab); don't stall forever
            if len(c.in_flight) >= MAX_IN_FLIGHT:
                continue                 # the next ack wakes us; we then send the newest frame
            c.last_frame_id = frame.id
            c.in_flight[frame.id] = (now, frame.ts)
            await c.ws.send_bytes(FRAME_HEADER.pack(frame.id, frame.ts, frame.width, frame.height) + frame.data)
            c.window_frames += 1
            c.window_bytes += FRAME_HEADER.size + len(frame.data)

    # ----- messages from a device
    def handle(self, c, msg):
        t = msg.get("t")
        if t == "ack":
            entry = c.in_flight.pop(msg.get("id"), None)
            if entry:
                sent, captured = entry
                queued = sent - captured   # > 1 s means a cached frame of a static screen, not delay
                lat = time.time() * 1000 - sent + (queued if queued < 1000 else 0)
                c.latency = lat if c.latency is None else c.latency * 0.8 + lat * 0.2
            c.wake.set()
        elif t == "ping":
            c.send({"t": "pong", "id": msg.get("id")})
        elif t == "cstat":
            c.rtt, c.device_fps = _num(msg.get("rtt")), _num(msg.get("fps"))
        elif t in ("pause", "resume"):
            c.paused = t == "pause"
            c.wake.set()
            self.update_wanted()
            self.admin_dirty.set()
        elif t == "monitor":
            index = msg.get("i")
            if _is_int(index) and 0 <= index < len(self.monitors):
                c.monitor = index
                c.last_frame_id = 0
                c.in_flight.clear()
                self.update_wanted()
                self.capturer.refresh(index)
                self.push_cursor(c)
                self.admin_dirty.set()
        elif t in INPUT_TYPES:
            if self.can_control(c) and not c.paused:
                self.inject(c, t, msg)

    def to_desktop(self, c, nx, ny):
        mons = self.monitors
        m = mons[c.monitor] if c.monitor < len(mons) else dict(left=0, top=0, width=1, height=1)
        return m["left"] + _clamp01(nx) * (m["width"] - 1), m["top"] + _clamp01(ny) * (m["height"] - 1)

    def inject(self, c, t, msg):
        self.capturer.poke()  # the screen is about to change; leave idle polling
        if t == "move":
            inputctl.move_to(*self.to_desktop(c, _num(msg.get("x"), 0), _num(msg.get("y"), 0)))
        elif t == "rel":
            inputctl.move_by(_num(msg.get("dx"), 0), _num(msg.get("dy"), 0))
        elif t in ("down", "up"):
            b = msg.get("b", 0)
            if b in (0, 1, 2):
                if "x" in msg:
                    inputctl.move_to(*self.to_desktop(c, _num(msg.get("x"), 0), _num(msg.get("y"), 0)))
                inputctl.button(b, t == "down")
                (c.buttons.add if t == "down" else c.buttons.discard)(b)
        elif t == "wheel":
            inputctl.wheel(_clampi(msg.get("dx"), 2400), _clampi(msg.get("dy"), 2400))
        elif t == "text":
            text = msg.get("s")
            if isinstance(text, str):
                inputctl.type_text(text[:2000])
        elif t == "key":
            k, mods = msg.get("k"), msg.get("mods") or []
            if isinstance(k, str) and isinstance(mods, list):
                inputctl.key_combo(k, [m for m in mods if m in inputctl.MODIFIERS])

    def release_inputs(self, c):
        for b in list(c.buttons):
            inputctl.button(b, False)
        c.buttons.clear()

    # ----- admin
    def state(self):
        now = time.time()
        online = {c.device_id for c in self.clients.values()}
        return {
            "host": self.host, "port": self.port, "ips": self.ips,
            "url": f"http://{self.ips[0]}:{self.port}/",
            "hostUrl": f"http://{self.host}.local:{self.port}/",
            "code": self.pairing_code,
            "settings": self.settings,
            "monitors": self.monitor_list(),
            "encodeMs": round(self.capturer.encode_ms, 1),
            "clients": [dict(
                id=c.id, deviceId=c.device_id, name=self.store.devices.get(c.device_id, {}).get("name", "?"),
                ip=c.ip, monitor=c.monitor, paused=c.paused, fps=c.fps, kbps=c.kbps,
                deviceFps=c.device_fps, rtt=_round(c.rtt), latency=_round(c.latency),
                quality=connection_quality(c.rtt, c.latency), since=c.connected_at,
                control=self.can_control(c)) for c in self.clients.values()],
            "devices": [dict(id=i, name=d["name"], pairedAt=d["paired_at"], lastSeen=d["last_seen"],
                             online=i in online, allowControl=d.get("allow_control", True))
                        for i, d in sorted(self.store.devices.items(), key=lambda kv: -kv[1]["last_seen"])],
            "requests": [dict(id=i, name=r["name"], ip=r["ip"], age=round(now - r["created"]))
                         for i, r in self.requests.items() if r["status"] == "pending"],
        }

    def admin_command(self, cmd):
        kind = cmd.get("cmd")
        if kind == "set":
            key, value = cmd.get("key"), cmd.get("value")
            if key in SETTING_RULES and SETTING_RULES[key](value):
                self.settings[key] = value
                self.store.save_settings()
                if key in ("quality", "max_width"):
                    for m in self.capturer.wanted:
                        self.capturer.refresh(m)
                if key in ("streaming", "allow_control"):
                    self.update_wanted()
                    for c in self.clients.values():
                        self.push_state(c)
                        if key == "streaming" and value:
                            c.last_frame_id = 0
                            c.wake.set()
        elif kind == "regen_code":
            self.pairing_code, self.failed_pairings = new_code(), 0
        elif kind == "kick":
            c = self.clients.get(cmd.get("client"))
            if c:
                asyncio.ensure_future(c.ws.close(code=4000, message=b"Disconnected by host"))
        elif kind == "revoke":
            device_id = cmd.get("device")
            if self.store.devices.pop(device_id, None):
                self.store.save_devices()
                for c in list(self.clients.values()):
                    if c.device_id == device_id:
                        asyncio.ensure_future(c.ws.close(code=4001, message=b"Pairing removed by host"))
        elif kind == "device_control":
            dev = self.store.devices.get(cmd.get("device"))
            if dev and isinstance(cmd.get("value"), bool):
                dev["allow_control"] = cmd["value"]
                self.store.save_devices()
                for c in self.clients.values():
                    if c.device_id == cmd.get("device"):
                        self.push_state(c)
        elif kind in ("approve", "deny"):
            req = self.requests.get(cmd.get("request"))
            if req and req["status"] == "pending":
                if kind == "approve":
                    req["device_id"], req["token"] = self.store.add_device(req["name"])
                req["status"] = "approved" if kind == "approve" else "denied"
        self.admin_dirty.set()

    async def admin_pusher(self):
        while True:
            await self.admin_dirty.wait()
            self.admin_dirty.clear()
            if self.admins:
                payload = json.dumps(self.state())
                for ws in list(self.admins):
                    try:
                        await ws.send_str(payload)
                    except (ConnectionError, RuntimeError):
                        self.admins.discard(ws)

    async def ticker(self):
        """Once a second: roll up per-client stats, expire pairing requests, refresh dashboards."""
        while True:
            await asyncio.sleep(1)
            for c in self.clients.values():
                c.fps, c.kbps = c.window_frames, round(c.window_bytes * 8 / 1000)
                c.window_frames = c.window_bytes = 0
                c.send({"t": "stats", "fps": c.fps, "kbps": c.kbps, "latency": _round(c.latency),
                        "encodeMs": round(self.capturer.encode_ms, 1)})
            now = time.time()
            for rid in [i for i, r in self.requests.items() if now - r["created"] > 180]:
                del self.requests[rid]
            self.admin_dirty.set()


INPUT_TYPES = {"move", "rel", "down", "up", "wheel", "text", "key"}


def _num(v, default=None):
    return float(v) if isinstance(v, (int, float)) and v == v else default


def _round(v):
    return None if v is None else round(v)


def _clamp01(v):
    return min(max(v, 0.0), 1.0)


def _clampi(v, limit):
    return int(min(max(_num(v, 0), -limit), limit))


# --------------------------------------------------------------------------- HTTP / WebSocket routes

routes = web.RouteTableDef()


def hub_of(request) -> Hub:
    return request.app["hub"]


def local_only(handler):
    async def wrapper(request):
        if not is_local(request):
            raise web.HTTPForbidden(text="The dashboard is only available on this PC.")
        return await handler(request)
    return wrapper


@routes.get("/")
async def index(request):
    return web.FileResponse(WEB / "client.html", headers={"Cache-Control": "no-cache"})


@routes.get("/admin")
@local_only
async def admin_page(request):
    return web.FileResponse(WEB / "admin.html", headers={"Cache-Control": "no-cache"})


@routes.get("/api/state")
@local_only
async def api_state(request):
    return web.json_response(hub_of(request).state())


@routes.get("/api/qr.svg")
@local_only
async def api_qr(request):
    hub = hub_of(request)
    ip = request.query.get("ip")
    ip = ip if ip in hub.ips else hub.ips[0]
    url = f"http://{ip}:{hub.port}/?code={hub.pairing_code}"
    img = qrcode.make(url, image_factory=qrcode.image.svg.SvgPathImage, border=2, box_size=10)
    return web.Response(body=img.to_string(), content_type="image/svg+xml",
                        headers={"Cache-Control": "no-store"})


@routes.post("/api/pair")
async def api_pair(request):
    hub = hub_of(request)
    body = await _json_body(request)
    code = str(body.get("code", "")).strip()
    if not secrets.compare_digest(code.encode(), hub.pairing_code.encode()):
        hub.failed_pairings += 1
        if hub.failed_pairings >= MAX_FAILED_PAIRINGS:  # brute-force guard: rotate the code
            hub.pairing_code, hub.failed_pairings = new_code(), 0
        hub.admin_dirty.set()
        return web.json_response({"error": "That code is not valid. Check the code shown on your PC."}, status=403)
    device_id, token = hub.store.add_device(body.get("name"))
    hub.pairing_code, hub.failed_pairings = new_code(), 0  # one code, one device
    hub.admin_dirty.set()
    print(f"[pair] {clean_name(body.get('name'))} paired from {request.remote}")
    return web.json_response({"token": token, "deviceId": device_id})


@routes.post("/api/request")
async def api_request(request):
    hub = hub_of(request)
    body = await _json_body(request)
    if sum(r["status"] == "pending" for r in hub.requests.values()) >= 10:
        return web.json_response({"error": "Too many pending requests. Try again shortly."}, status=429)
    rid = secrets.token_urlsafe(12)
    hub.requests[rid] = dict(name=clean_name(body.get("name")), ip=request.remote,
                             created=time.time(), status="pending")
    hub.admin_dirty.set()
    return web.json_response({"id": rid})


@routes.get("/api/request/{rid}")
async def api_request_status(request):
    hub = hub_of(request)
    rid = request.match_info["rid"]
    req = hub.requests.get(rid)
    if not req:
        return web.json_response({"status": "expired"})
    if req["status"] == "pending":
        return web.json_response({"status": "pending"})
    # answered requests stay until they expire, so a poll lost on flaky Wi-Fi can be retried
    if req["status"] == "approved":
        return web.json_response({"status": "approved", "token": req["token"], "deviceId": req["device_id"]})
    return web.json_response({"status": "denied"})


@routes.delete("/api/request/{rid}")
async def api_request_cancel(request):
    hub = hub_of(request)
    req = hub.requests.get(request.match_info["rid"])
    if req and req["status"] == "pending":
        del hub.requests[request.match_info["rid"]]
        hub.admin_dirty.set()
    return web.json_response({"status": "cancelled"})


@routes.get("/ws")
async def ws_client(request):
    hub = hub_of(request)
    ws = web.WebSocketResponse(heartbeat=10, max_msg_size=1 << 20)
    await ws.prepare(request)
    try:
        first = await asyncio.wait_for(ws.receive(), 10)
        auth = json.loads(first.data) if first.type == WSMsgType.TEXT else {}
    except (asyncio.TimeoutError, ValueError):
        auth = {}
    if not isinstance(auth, dict):
        auth = {}
    device_id = hub.store.device_for_token(auth.get("token")) if auth.get("t") == "auth" else None
    if not device_id:
        await ws.close(code=4001, message=b"Not paired")
        return ws

    c = Client(ws, device_id, request.remote, hub.default_monitor())
    if _is_int(auth.get("monitor")) and 0 <= auth["monitor"] < len(hub.monitors):
        c.monitor = auth["monitor"]
    c.paused = bool(auth.get("paused"))
    dev = hub.store.devices[device_id]
    dev["last_seen"] = time.time()
    hub.store.save_devices()
    hub.clients[c.id] = c
    print(f"[connect] {dev['name']} ({c.ip})")

    c.send({"t": "hello", "client": c.id, "name": dev["name"], "host": hub.host,
            "monitors": hub.monitor_list(), "monitor": c.monitor})
    hub.push_state(c)
    hub.push_cursor(c)
    sender = asyncio.ensure_future(hub.sender(c))
    hub.update_wanted()
    hub.capturer.refresh(c.monitor)
    hub.admin_dirty.set()
    try:
        async for msg in ws:
            if msg.type != WSMsgType.TEXT:
                continue
            try:
                data = json.loads(msg.data)
            except ValueError:
                continue
            if isinstance(data, dict):
                try:
                    hub.handle(c, data)
                except Exception as exc:  # a bad message must not drop the connection
                    print(f"[client] {data.get('t')!r}: {exc!r}")
    finally:
        sender.cancel()
        hub.release_inputs(c)
        hub.clients.pop(c.id, None)
        if device_id in hub.store.devices:
            hub.store.devices[device_id]["last_seen"] = time.time()
            hub.store.save_devices()
        hub.update_wanted()
        hub.admin_dirty.set()
        print(f"[disconnect] {dev['name']} ({c.ip})")
    return ws


@routes.get("/ws/admin")
@local_only
async def ws_admin(request):
    hub = hub_of(request)
    ws = web.WebSocketResponse(heartbeat=20)
    await ws.prepare(request)
    hub.admins.add(ws)
    await ws.send_str(json.dumps(hub.state()))
    try:
        async for msg in ws:
            if msg.type == WSMsgType.TEXT:
                try:
                    cmd = json.loads(msg.data)
                except ValueError:
                    continue
                if isinstance(cmd, dict):
                    hub.admin_command(cmd)
    finally:
        hub.admins.discard(ws)
    return ws


async def _json_body(request):
    try:
        body = await request.json()
        return body if isinstance(body, dict) else {}
    except ValueError:
        return {}


# --------------------------------------------------------------------------- app

def build_app(port):
    app = web.Application(client_max_size=64 * 1024)
    app.add_routes(routes)
    app.router.add_static("/static", WEB, append_version=False)
    app["hub"] = hub = Hub(Store(), port)
    tasks = []

    async def on_startup(app):
        hub.loop = asyncio.get_running_loop()
        hub.capturer.start()
        tasks.extend([asyncio.ensure_future(hub.ticker()), asyncio.ensure_future(hub.admin_pusher())])

    async def on_shutdown(app):
        hub.capturer.stop()
        for t in tasks:
            t.cancel()
        for c in list(hub.clients.values()):
            await c.ws.close(code=1001, message=b"Host shutting down")
        for ws in list(hub.admins):
            await ws.close(code=1001)

    app.on_startup.append(on_startup)
    app.on_shutdown.append(on_shutdown)
    return app


def main():
    parser = argparse.ArgumentParser(description="ScreenMirror host")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--no-browser", action="store_true", help="don't open the dashboard on start")
    args = parser.parse_args()

    if sys.platform != "win32":
        sys.exit("ScreenMirror host currently supports Windows only.")
    inputctl.enable_dpi_awareness()

    app = build_app(args.port)
    hub = app["hub"]
    admin_url = f"http://localhost:{args.port}/admin"
    print("\n  ScreenMirror is running")
    print(f"  Dashboard : {admin_url}")
    for ip in hub.ips:
        print(f"  On phone  : http://{ip}:{args.port}/")
    print(f"  Pair code : {hub.pairing_code}   (Ctrl+C to quit)\n")
    if not args.no_browser:
        async def open_dashboard(_):
            asyncio.get_running_loop().call_later(0.5, webbrowser.open, admin_url)
        app.on_startup.append(open_dashboard)
    try:
        web.run_app(app, host="0.0.0.0", port=args.port, print=None, access_log=None)
    except OSError as exc:
        sys.exit(f"Could not listen on port {args.port}: {exc}. Is ScreenMirror already running? Try --port.")


if __name__ == "__main__":
    main()
