"""Zero-dependency screen grab: ctypes GDI capture + hand-rolled PNG encoder.

Usage: python grab.py <output.png>
Captures the primary screen. PNG encoding via zlib (stdlib) only.
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import zlib

u32 = ctypes.windll.user32
g32 = ctypes.windll.gdi32
k32 = ctypes.windll.kernel32

u32.SetProcessDPIAware()
w = u32.GetSystemMetrics(0)   # SM_CXSCREEN
h = u32.GetSystemMetrics(1)   # SM_CYSCREEN

hdc = u32.GetDC(None)
mem = g32.CreateCompatibleDC(hdc)
bmp = g32.CreateCompatibleBitmap(hdc, w, h)
g32.SelectObject(mem, bmp)
g32.BitBlt(mem, 0, 0, w, h, hdc, 0, 0, 0x00CC0020)  # SRCCOPY


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
bmi.biHeight = -h  # top-down
bmi.biPlanes = 1
bmi.biBitCount = 32
bmi.biCompression = 0  # BI_RGB
bmi.biSizeImage = w * h * 4

buf = ctypes.create_string_buffer(w * h * 4)
ok = g32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bmi), 0)
if not ok:
    print("GetDIBits failed", file=sys.stderr)
    sys.exit(1)

g32.DeleteObject(bmp)
g32.DeleteDC(mem)
u32.ReleaseDC(None, hdc)

raw = buf.raw  # BGRA rows

# Build PNG: RGB8, filter 0 per scanline
stride = w * 4
lines = []
for y in range(h):
    row = raw[y * stride:(y + 1) * stride]
    # BGRA -> RGB
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

out = sys.argv[1] if len(sys.argv) > 1 else "grab.png"
with open(out, "wb") as f:
    f.write(png)
print(f"saved {out} {w}x{h}")
