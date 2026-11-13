"""生成应用图标与打包用 Logo 资源（纯 Python，无第三方依赖）。

图标含义：圆角方块 + 渐变底色 + 三根白色「右键菜单」条目。
运行： python tools/make_assets.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

TOP = (0x4F, 0x8C, 0xFF)      # 亮蓝
BOTTOM = (0x1D, 0x4E, 0xD8)   # 深蓝
WHITE = (0xFF, 0xFF, 0xFF)


def _lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def _rounded_rect_coverage(x, y, w, h, r):
    """点 (x, y) 落在圆角矩形 [0, w] x [0, h] 内返回 True。"""
    cx = min(max(x, r), w - r)
    cy = min(max(y, r), h - r)
    dx = x - cx
    dy = y - cy
    return dx * dx + dy * dy <= r * r


def render(size, supersample=4):
    """返回 RGBA bytes（size x size）。"""
    s = supersample
    n = size * s
    radius = 0.22 * n
    # 三根菜单条：左端对齐，宽度递减
    bars = [
        (0.24, 0.34, 0.52, 0.075),
        (0.24, 0.475, 0.40, 0.075),
        (0.24, 0.61, 0.28, 0.075),
    ]
    bars_px = [(bx * n, by * n, bw * n, bh * n) for bx, by, bw, bh in bars]
    bar_r = 0.0375 * n

    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            acc_r = acc_g = acc_b = acc_a = 0
            for sy in range(s):
                for sx in range(s):
                    x = px * s + sx + 0.5
                    y = py * s + sy + 0.5
                    if not _rounded_rect_coverage(x, y, n, n, radius):
                        continue
                    # 底色渐变
                    t = y / n
                    cr, cg, cb = _lerp(TOP, BOTTOM, t)
                    # 白色菜单条
                    for bx, by, bw, bh in bars_px:
                        if (bx <= x <= bx + bw and by <= y <= by + bh
                                and _rounded_rect_coverage(x - bx, y - by, bw, bh, min(bar_r, bh / 2))):
                            cr, cg, cb = WHITE
                            break
                    acc_r += cr
                    acc_g += cg
                    acc_b += cb
                    acc_a += 255
            total = s * s
            if acc_a == 0:
                row += b"\x00\x00\x00\x00"
            else:
                # 以白色为底做一次预乘处理，避免边缘发暗
                a = acc_a / total
                row += bytes((
                    int(acc_r / (acc_a / 255)),
                    int(acc_g / (acc_a / 255)),
                    int(acc_b / (acc_a / 255)),
                    int(round(a)),
                ))
        rows.append(bytes(row))
    return b"".join(rows)


def png_bytes(size):
    raw = render(size)
    stride = size * 4
    data = b"".join(b"\x00" + raw[y * stride:(y + 1) * stride] for y in range(size))

    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(data, 9)) + chunk(b"IEND", b""))


def write(path, payload):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(payload)
    print("wrote", os.path.relpath(path, ROOT), len(payload), "bytes")


def ico_bytes(sizes):
    images = [(s, png_bytes(s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    blobs = b""
    for s, blob in images:
        entries += struct.pack("<BBBBHHII", 0 if s >= 256 else s, 0 if s >= 256 else s,
                               0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        blobs += blob
    return header + entries + blobs


def wide_logo(w, h, icon_size):
    """在透明画布中央放置图标（RGBA）。"""
    raw = render(icon_size)
    out = bytearray(b"\x00" * (w * h * 4))
    ox = (w - icon_size) // 2
    oy = (h - icon_size) // 2
    for y in range(icon_size):
        for x in range(icon_size):
            i = (y * icon_size + x) * 4
            a = raw[i + 3]
            if not a:
                continue
            o = ((oy + y) * w + ox + x) * 4
            out[o:o + 4] = raw[i:i + 4]
    stride = w * 4
    data = b"".join(b"\x00" + bytes(out[y * stride:(y + 1) * stride]) for y in range(h))

    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(data, 9)) + chunk(b"IEND", b""))


def main():
    app_assets = os.path.join(ROOT, "src", "7zAIWin11RCM", "Assets")
    pkg_assets = os.path.join(ROOT, "packaging", "Assets")

    write(os.path.join(app_assets, "app.ico"), ico_bytes([16, 24, 32, 48, 64, 128, 256]))
    for s in (32, 64, 256):
        write(os.path.join(app_assets, "app-%d.png" % s), png_bytes(s))
    for s in (44, 50, 150):
        write(os.path.join(pkg_assets, "Square%dLogo.png" % s), png_bytes(s))
    write(os.path.join(pkg_assets, "StoreLogo.png"), png_bytes(50))
    write(os.path.join(pkg_assets, "Wide310x150Logo.png"), wide_logo(310, 150, 96))


if __name__ == "__main__":
    main()
