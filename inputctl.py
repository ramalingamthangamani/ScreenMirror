"""Windows input injection via SendInput (ctypes, no dependencies).

All coordinates are physical pixels in virtual-desktop space; the process must be
DPI aware (see enable_dpi_awareness) so they line up with the captured frames.
"""
import ctypes
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)

INPUT_MOUSE, INPUT_KEYBOARD = 0, 1

MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP = 0x0002, 0x0004
MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP = 0x0008, 0x0010
MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP = 0x0020, 0x0040
MOUSEEVENTF_WHEEL, MOUSEEVENTF_HWHEEL = 0x0800, 0x1000
MOUSEEVENTF_VIRTUALDESK, MOUSEEVENTF_ABSOLUTE = 0x4000, 0x8000

KEYEVENTF_EXTENDEDKEY, KEYEVENTF_KEYUP, KEYEVENTF_UNICODE = 0x1, 0x2, 0x4

SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN = 76, 77
SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN = 78, 79

ULONG_PTR = wintypes.WPARAM


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG), ("mouseData", wintypes.DWORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", wintypes.DWORD), ("wParamL", wintypes.WORD), ("wParamH", wintypes.WORD)]


class _INPUTUNION(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _INPUTUNION)]


user32.SendInput.argtypes = (wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int)
user32.SendInput.restype = wintypes.UINT
user32.GetCursorPos.argtypes = (ctypes.POINTER(wintypes.POINT),)
user32.MapVirtualKeyW.argtypes = (wintypes.UINT, wintypes.UINT)
user32.MapVirtualKeyW.restype = wintypes.UINT
user32.VkKeyScanW.argtypes = (wintypes.WCHAR,)
user32.VkKeyScanW.restype = ctypes.c_short


def enable_dpi_awareness():
    """Use physical pixels everywhere (per-monitor v2, with fallbacks for older Windows)."""
    try:
        if user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
            return
    except AttributeError:
        pass
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        user32.SetProcessDPIAware()


def _send(inputs):
    if not inputs:
        return
    arr = (INPUT * len(inputs))(*inputs)
    user32.SendInput(len(inputs), arr, ctypes.sizeof(INPUT))


def _mouse(flags, dx=0, dy=0, data=0):
    inp = INPUT(type=INPUT_MOUSE)
    inp.mi = MOUSEINPUT(dx, dy, data & 0xFFFFFFFF, flags, 0, 0)
    return inp


def _key(vk=0, scan=0, flags=0):
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.ki = KEYBDINPUT(vk, scan, flags, 0, 0)
    return inp


# ---------------------------------------------------------------- mouse

def virtual_screen():
    g = user32.GetSystemMetrics
    return g(SM_XVIRTUALSCREEN), g(SM_YVIRTUALSCREEN), g(SM_CXVIRTUALSCREEN), g(SM_CYVIRTUALSCREEN)


def cursor_pos():
    pt = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


def move_to(x, y):
    vx, vy, vw, vh = virtual_screen()
    x = min(max(int(round(x)), vx), vx + vw - 1)
    y = min(max(int(round(y)), vy), vy + vh - 1)
    nx = round((x - vx) * 65535 / max(vw - 1, 1))
    ny = round((y - vy) * 65535 / max(vh - 1, 1))
    _send([_mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny)])


def move_by(dx, dy):
    x, y = cursor_pos()
    move_to(x + dx, y + dy)


_BUTTON_FLAGS = {
    0: (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    1: (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
    2: (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
}


def button(btn, down):
    flags = _BUTTON_FLAGS.get(btn)
    if flags:
        _send([_mouse(flags[0] if down else flags[1])])


def wheel(dx, dy):
    """dy > 0 scrolls up, dx > 0 scrolls right (120 = one notch)."""
    events = []
    if dy:
        events.append(_mouse(MOUSEEVENTF_WHEEL, data=int(dy)))
    if dx:
        events.append(_mouse(MOUSEEVENTF_HWHEEL, data=int(dx)))
    _send(events)


# ---------------------------------------------------------------- keyboard

VK = {
    "Backspace": 0x08, "Tab": 0x09, "Enter": 0x0D, "Shift": 0x10, "Control": 0x11, "Alt": 0x12,
    "Pause": 0x13, "CapsLock": 0x14, "Escape": 0x1B, " ": 0x20, "Space": 0x20,
    "PageUp": 0x21, "PageDown": 0x22, "End": 0x23, "Home": 0x24,
    "ArrowLeft": 0x25, "ArrowUp": 0x26, "ArrowRight": 0x27, "ArrowDown": 0x28,
    "PrintScreen": 0x2C, "Insert": 0x2D, "Delete": 0x2E, "Meta": 0x5B, "ContextMenu": 0x5D,
    "NumLock": 0x90, "ScrollLock": 0x91,
    "AudioVolumeMute": 0xAD, "AudioVolumeDown": 0xAE, "AudioVolumeUp": 0xAF,
    "MediaTrackNext": 0xB0, "MediaTrackPrevious": 0xB1, "MediaStop": 0xB2, "MediaPlayPause": 0xB3,
    **{f"F{i}": 0x6F + i for i in range(1, 13)},
}
_EXTENDED = {0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D,
             0x90, 0xA3, 0xA5, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3}
MODIFIERS = {"ctrl": 0x11, "shift": 0x10, "alt": 0x12, "meta": 0x5B}


def _vk_event(vk, up):
    flags = (KEYEVENTF_KEYUP if up else 0) | (KEYEVENTF_EXTENDEDKEY if vk in _EXTENDED else 0)
    return _key(vk, user32.MapVirtualKeyW(vk, 0) & 0xFF, flags)


def resolve_key(name):
    """Map a DOM KeyboardEvent.key value to (vk, needs_shift). Returns None if unknown."""
    if name in VK:
        return VK[name], False
    if len(name) == 1:
        res = user32.VkKeyScanW(name)
        if res != -1:
            return res & 0xFF, bool(res & 0x100)
    return None


def key_combo(name, mods=()):
    """Press and release `name` while holding the given modifiers (ctrl/shift/alt/meta)."""
    resolved = resolve_key(name)
    if resolved is None:
        if len(name) == 1 and not mods:
            type_text(name)
        return
    vk, needs_shift = resolved
    mod_vks = [MODIFIERS[m] for m in mods if m in MODIFIERS]
    if needs_shift and 0x10 not in mod_vks:
        mod_vks.append(0x10)
    events = [_vk_event(m, False) for m in mod_vks]
    events += [_vk_event(vk, False), _vk_event(vk, True)]
    events += [_vk_event(m, True) for m in reversed(mod_vks)]
    _send(events)


def key_state(vk, down):
    _send([_vk_event(vk, not down)])


def type_text(text):
    events = []
    for ch in text:
        if ch == "\r":
            continue
        if ch in "\n\t":
            vk = 0x0D if ch == "\n" else 0x09
            events += [_vk_event(vk, False), _vk_event(vk, True)]
            continue
        data = ch.encode("utf-16-le")
        for i in range(0, len(data), 2):  # surrogate pairs are sent as two units
            unit = int.from_bytes(data[i:i + 2], "little")
            events += [_key(0, unit, KEYEVENTF_UNICODE), _key(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)]
    _send(events)
