"""Capture a specific window's content via PrintWindow (works when occluded).

Usage: python grabwin.py <output.png> [window_title_substring]
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import zlib

u32 = ctypes.windll.user32
g32 = ctypes.windll.gdi32

u32.SetProcessDPIAware()
PW_RENDERFULLCONTENT = 0x00000002


def find_window(sub):
    result = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(hwnd, _):
        if not u32.IsWindowVisible(hwnd):
            return True
        n = u32.GetWindowTextLengthW(hwnd)
        if n == 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        u32.GetWindowTextW(hwnd, buf, n + 1)
        if sub in buf.value:
            result.append((hwnd, buf.value))
        return True

    u32.EnumWindows(cb, None)
    return result[0] if result else None


sub = sys.argv[2] if len(sys.argv) > 2 else "ClassIntraOps"
found = find_window(sub)
if not found:
    print("window not found:", sub)
    sys.exit(1)
hwnd, title = found

rect = wt.RECT()
u32.GetWindowRect(hwnd, ctypes.byref(rect))
w = rect.right - rect.left
h = rect.bottom - rect.top
if w <= 0 or h <= 0:
    print("bad size", w, h)
    sys.exit(1)

hdc = u32.GetDC(None)
mem = g32.CreateCompatibleDC(hdc)
bmp = g32.CreateCompatibleBitmap(hdc, w, h)
g32.SelectObject(mem, bmp)
# PrintWindow with RENDERFULLCONTENT renders the window even if occluded
ok = u32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)
if not ok:
    print("PrintWindow failed")
    sys.exit(1)


class BMIHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


bmi = BMIHEADER()
bmi.biSize = ctypes.sizeof(BMIHEADER)
bmi.biWidth = w
bmi.biHeight = -h
bmi.biPlanes = 1
bmi.biBitCount = 32
bmi.biCompression = 0
bmi.biSizeImage = w * h * 4

buf = ctypes.create_string_buffer(w * h * 4)
if not g32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bmi), 0):
    print("GetDIBits failed")
    sys.exit(1)

g32.DeleteObject(bmp)
g32.DeleteDC(mem)
u32.ReleaseDC(None, hdc)

raw = buf.raw
# BGRA -> RGB, drop alpha, 2px border crop to remove window chrome edges
stride = w * 4
lines = []
for y in range(h):
    row = raw[y * stride:(y + 1) * stride]
    rgb = bytearray(w * 3)
    for i in range(w):
        rgb[i * 3] = row[i * 4 + 2]
        rgb[i * 3 + 1] = row[i * 4 + 1]
        rgb[i * 3 + 2] = row[i * 4]
    lines.append(b"\x00" + bytes(rgb))
raw_png = b"".join(lines)


def chunk(tag, data):
    c = struct.pack(">I", len(data)) + tag + data
    return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)


ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
png = (b"\x89PNG\r\n\x1a\n"
       + chunk(b"IHDR", ihdr)
       + chunk(b"IDAT", zlib.compress(raw_png, 6))
       + chunk(b"IEND", b""))

out = sys.argv[1]
with open(out, "wb") as f:
    f.write(png)
print(f"saved {out} {w}x{h} [{title}]")
