"""Find all pixels close to lime #A3E635 in a PNG and report clusters."""
import struct
import sys
import zlib


def read_png(path):
    with open(path, "rb") as f:
        data = f.read()
    pos = 8
    w = h = None
    idat = b""
    while pos < len(data):
        ln = struct.unpack(">I", data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + ln]
        if tag == b"IHDR":
            w, h, bit, color = struct.unpack(">IIBB", chunk[:10])
        elif tag == b"IDAT":
            idat += chunk
        pos += 12 + ln
    raw = zlib.decompress(idat)
    stride = w * 3
    rows = []
    prev = bytearray(stride)
    p = 0
    for _ in range(h):
        ft = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        if ft == 1:
            for i in range(3, stride):
                line[i] = (line[i] + line[i - 3]) & 0xFF
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:
            for i in range(stride):
                a = line[i - 3] if i >= 3 else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ft == 4:
            for i in range(stride):
                a = line[i - 3] if i >= 3 else 0
                b = prev[i]
                c = prev[i - 3] if i >= 3 else 0
                pp = a + b - c
                pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        rows.append(line)
        prev = line
    return w, h, rows


w, h, rows = read_png(sys.argv[1])
hits = []
for y in range(0, h, 2):
    row = rows[y]
    for x in range(0, w, 2):
        i = x * 3
        r, g, b = row[i], row[i + 1], row[i + 2]
        # lime family: green dominant, high green, low-mid blue
        if g > 150 and r < 220 and b < 120 and g - b > 60 and g - r < 90:
            hits.append((x, y))

print(f"total lime-ish pixels: {len(hits)}")
# cluster by rough y bands
from collections import Counter
bands = Counter()
for x, y in hits:
    bands[y // 50 * 50] += 1
for band in sorted(bands):
    xs = [x for x, y in hits if band <= y < band + 50]
    print(f"y~{band}-{band + 49}: n={bands[band]} x-range={min(xs)}-{max(xs)}")
