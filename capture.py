"""Screen capture thread: grabs the monitors someone is watching, skips unchanged
frames, JPEG-encodes the rest and hands them to the asyncio loop.

Grabbing (GDI) and JPEG encoding both release the GIL, so encoding runs on a second
thread while the next frame is grabbed."""
import ctypes
import io
import threading
import time
from concurrent.futures import ThreadPoolExecutor

import mss
from PIL import Image

import inputctl

MSS = getattr(mss, "MSS", None) or mss.mss
IDLE_AFTER = 1.0       # seconds without screen changes before polling slows down
IDLE_INTERVAL = 0.1    # polling interval while idle (any input wakes it immediately)


class Frame:
    __slots__ = ("id", "monitor", "ts", "width", "height", "data")

    def __init__(self, fid, monitor, ts, width, height, data):
        self.id, self.monitor, self.ts = fid, monitor, ts
        self.width, self.height, self.data = width, height, data


def list_monitors(sct):
    """mss index 0 is the whole virtual desktop; 1..n are the physical monitors."""
    return [dict(index=i, left=m["left"], top=m["top"], width=m["width"], height=m["height"])
            for i, m in enumerate(sct.monitors)]


class Capturer(threading.Thread):
    def __init__(self, settings, on_frame, on_cursor, on_monitors):
        super().__init__(name="capture", daemon=True)
        self.settings = settings          # dict with fps / quality / max_width (read live)
        self.on_frame = on_frame          # on_frame(Frame), called from the encoder thread
        self.on_cursor = on_cursor        # on_cursor(x, y) in physical desktop pixels
        self.on_monitors = on_monitors    # on_monitors(list) when the layout changes
        self.wanted = frozenset()         # monitor indices with active viewers
        self.monitors = []
        self.encode_ms = 0.0
        self._refresh = set()
        self._stop = threading.Event()
        self._wake = threading.Event()
        self._next_id = 1
        self._last_change = 0.0

    def stop(self):
        self._stop.set()
        self._wake.set()

    def refresh(self, monitor):
        """Force the next grab of `monitor` to be sent even if unchanged (new viewer)."""
        self._refresh.add(monitor)
        self.poke()

    def poke(self):
        """Something is happening (input, new viewer): poll at full rate right away."""
        self._last_change = time.perf_counter()
        self._wake.set()

    def run(self):
        ctypes.windll.winmm.timeBeginPeriod(1)  # 1 ms sleep granularity instead of 15.6 ms
        encoder = ThreadPoolExecutor(1, thread_name_prefix="encode")
        pending = None
        last_raw = {}
        last_cursor = None
        sct = None
        layout_checked = 0.0
        while not self._stop.is_set():
            tick = time.perf_counter()
            try:
                if sct is None or tick - layout_checked > 3:
                    if sct is not None:
                        sct.close()
                    sct = MSS()
                    monitors = list_monitors(sct)
                    layout_checked = tick
                    if monitors != self.monitors:
                        self.monitors = monitors
                        last_raw.clear()
                        self.on_monitors(monitors)

                wanted = self.wanted
                if not wanted:
                    last_raw.clear()
                    self._wake.wait(0.2)
                    self._wake.clear()
                    continue

                cursor = inputctl.cursor_pos()
                if cursor != last_cursor:
                    last_cursor = cursor
                    self.on_cursor(*cursor)

                for index in wanted:
                    if index >= len(self.monitors):
                        continue
                    shot = sct.grab(sct.monitors[index])
                    if index in self._refresh:
                        self._refresh.discard(index)
                    elif last_raw.get(index) == shot.raw:
                        continue
                    last_raw[index] = shot.raw
                    self._last_change = time.perf_counter()
                    if pending is not None:
                        pending.result()          # at most one frame encoding at a time
                    pending = encoder.submit(self._encode, index, shot, time.time() * 1000)
            except Exception as exc:  # display change, locked session, etc. - retry
                print(f"[capture] {exc!r}")
                sct = None
                self._stop.wait(0.5)
                continue

            interval = 1 / max(1, int(self.settings.get("fps", 30)))
            if tick - self._last_change > IDLE_AFTER:
                interval = max(interval, IDLE_INTERVAL)
            self._wake.wait(max(0.0, interval - (time.perf_counter() - tick)))
            self._wake.clear()
        encoder.shutdown(wait=False)
        ctypes.windll.winmm.timeEndPeriod(1)

    def _encode(self, index, shot, ts):
        t0 = time.perf_counter()
        img = Image.frombuffer("RGB", shot.size, shot.raw, "raw", "BGRX", 0, 1)
        max_w = int(self.settings.get("max_width") or 0)
        if max_w and img.width > max_w:
            img = img.resize((max_w, round(img.height * max_w / img.width)), Image.BILINEAR)
        buf = io.BytesIO()
        img.save(buf, "JPEG", quality=int(self.settings.get("quality", 70)))
        self.encode_ms = self.encode_ms * 0.9 + (time.perf_counter() - t0) * 1000 * 0.1
        frame = Frame(self._next_id, index, ts, img.width, img.height, buf.getvalue())
        self._next_id += 1
        self.on_frame(frame)
