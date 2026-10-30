#!/usr/bin/env python3
"""生成 app.ico —— 纯标准库（zlib/struct），超采样抗锯齿。

图形：蓝色圆角方块 + 三根白色柱状（性能柱），与浮窗的白蓝主题一致。
"""
import os
import struct
import zlib


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def inside_round_rect(x, y, w, h, r):
    if x < 0 or y < 0 or x >= w or y >= h:
        return False
    cx = min(max(x, r), w - r)
    cy = min(max(y, r), h - r)
    dx = x - cx
    dy = y - cy
    return dx * dx + dy * dy <= r * r


def render(size, ss=4):
    """返回 size×size 的 RGBA bytearray。"""
    W = size * ss
    r = W * 0.235

    c1 = (0x2B, 0x62, 0xE8)
    c2 = (0x5C, 0x9B, 0xFF)

    # 柱子定义：(x0, x1, 顶部 y) 以 0..1 归一化
    bars = [(0.255, 0.395, 0.585), (0.430, 0.570, 0.395), (0.605, 0.745, 0.475)]
    base_y = 0.755

    acc = [[0, 0, 0, 0] for _ in range(size * size)]

    for Y in range(W):
        for X in range(W):
            if not inside_round_rect(X, Y, W, W, r):
                continue
            t = 0.35 * (X / W) + 0.65 * (Y / W)
            col = lerp(c1, c2, t)

            nx = X / W
            ny = Y / W
            for x0, x1, top in bars:
                if x0 <= nx <= x1 and top <= ny <= base_y:
                    # 柱子内部：白，边缘轻微柔化
                    edge = min(nx - x0, x1 - nx, ny - top, base_y - ny)
                    if edge * W < 1.2 * ss:
                        col = lerp(col, (255, 255, 255), 0.55)
                    else:
                        col = (255, 255, 255)
                    break

            i = (Y // ss) * size + (X // ss)
            acc[i][0] += col[0]
            acc[i][1] += col[1]
            acc[i][2] += col[2]
            acc[i][3] += 255

    n = ss * ss
    out = bytearray(size * size * 4)
    for i in range(size * size):
        a = acc[i][3] // n
        if a > 0:
            cov = acc[i][3] / 255.0   # 被覆盖的采样点数
            out[i * 4 + 0] = min(255, int(acc[i][0] / cov))
            out[i * 4 + 1] = min(255, int(acc[i][1] / cov))
            out[i * 4 + 2] = min(255, int(acc[i][2] / cov))
        out[i * 4 + 3] = a
    return out


def png_bytes(w, h, rgba):
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(bytes(raw), 9))
            + chunk(b'IEND', b''))


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out_path = os.path.abspath(os.path.join(here, '..', 'app.ico'))

    sizes = [16, 24, 32, 48, 64, 128, 256]
    entries = []
    for s in sizes:
        rgba = render(s)
        entries.append((s, png_bytes(s, s, rgba)))
        print(f'  {s}x{s} ok')

    header = struct.pack('<HHH', 0, 1, len(entries))
    dirs = b''
    data = b''
    offset = 6 + 16 * len(entries)
    for s, png in entries:
        dim = 0 if s >= 256 else s
        dirs += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
        data += png

    with open(out_path, 'wb') as f:
        f.write(header + dirs + data)

    print('written:', out_path, os.path.getsize(out_path), 'bytes')


if __name__ == '__main__':
    main()
