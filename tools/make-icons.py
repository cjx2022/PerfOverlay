#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""把两张 AI 出的 logo（浅色/深色）加工成程序用的图标。

做三件事：
1. 抠白底 —— 只抠「与画布边缘连通」的近白像素，卡片内部浅色区域不受影响。
2. 按内容包围盒裁切 + 统一留白 —— 两版用同一个包围盒，切换主题时视觉尺寸不跳。
3. 出 PNG（各种尺寸）+ 多尺寸 ICO（16/24/32/48/64/128/256）。

用法：python tools/make-icons.py
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageEnhance

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "assets")
SRC = {
    "light": r"C:\Users\bill\.workbuddy\clipboard-images\clipboard-2026-11-12T15-46-23-173Z-4fdfbbbd.jpg",
    "dark": r"C:\Users\bill\.workbuddy\clipboard-images\clipboard-2026-11-12T15-46-23-175Z-ea6e3af5.jpg",
}
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
WHITE_CUT = 236      # 三通道都 >= 这个值才当作背景白


def cut_background(src_path):
    """抠掉与边缘连通的近白背景，返回 RGBA。

    关键：alpha 由**连通性**决定，不由颜色深浅决定。
    卡片外框本身就是接近纯白的（#F0F6FC 这种），按「越白越透明」算会把它做成半透明，
    叠到深色任务栏上就成了一圈灰斑。所以这里只把 floodfill 连通到的背景判为透明，
    其余像素一律不透明，最后整体羽化 1px 让边缘柔和。
    """
    im = Image.open(src_path).convert("RGB")
    w, h = im.size

    marked = im.copy()
    for seed in [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)]:
        ImageDraw.floodfill(marked, seed, (255, 0, 255), thresh=14)

    out = im.convert("RGBA")
    px_src = im.load()
    px_mk = marked.load()
    px = out.load()
    for y in range(h):
        for x in range(w):
            r, g, b = px_mk[x, y]
            if r > 200 and g < 60 and b > 200:
                px[x, y] = (0, 0, 0, 0)
            else:
                sr, sg, sb = px_src[x, y]
                px[x, y] = (sr, sg, sb, 255)

    alpha = out.getchannel("A").filter(ImageFilter.GaussianBlur(0.9))
    out.putalpha(alpha)
    return out


def bbox_union(images):
    box = None
    for im in images:
        b = im.getbbox()
        if b is None:
            continue
        if box is None:
            box = list(b)
        else:
            box[0] = min(box[0], b[0])
            box[1] = min(box[1], b[1])
            box[2] = max(box[2], b[2])
            box[3] = max(box[3], b[3])
    return tuple(box)


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    cut = {k: cut_background(v) for k, v in SRC.items()}
    box = bbox_union(cut.values())
    # 两边都是正方形内容，取并集后补成正方形，再加 3% 留白
    side = max(box[2] - box[0], box[3] - box[1])
    pad = int(side * 0.03)
    side += pad * 2
    cx = (box[0] + box[2]) // 2
    cy = (box[1] + box[3]) // 2
    sq = (cx - side // 2, cy - side // 2, cx + side // 2, cy + side // 2)
    print("bbox", box, "-> square", sq, "side", side)

    master = {}
    for k, im in cut.items():
        canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        canvas.paste(im.crop(sq), (0, 0))
        master[k] = canvas
        # 源图只留 512 那版：够以后重新导尺寸，又不会把仓库撑到几 MB
        canvas.resize((512, 512), Image.LANCZOS).save(os.path.join(OUT, "logo-%s.png" % k))

    for k, im in master.items():
        frames = {}
        for s in ICO_SIZES:
            small = im.resize((s, s), Image.LANCZOS)
            if s <= 32:
                # 插画缩到 32px 以下细节会糊成一片，稍微提饱和 + 锐化，
                # 让折线还能从背景里跳出来。只加这一点，不改构图。
                small = ImageEnhance.Color(small).enhance(1.15)
                small = small.filter(ImageFilter.UnsharpMask(radius=1.0, percent=60, threshold=2))
            frames[s] = small
        ico = os.path.join(OUT, "PerfOverlay-%s.ico" % k)
        frames[256].save(ico, format="ICO",
                         sizes=[(s, s) for s in ICO_SIZES],
                         append_images=[frames[s] for s in ICO_SIZES if s != 256])
        print("wrote", ico, os.path.getsize(ico), "bytes")

    # 浅色版同时作为 exe 图标（资源管理器里浅色那版更清楚）
    exe_ico = os.path.join(ROOT, "app.ico")
    backup = os.path.join(ROOT, "_archive", "app.ico.bak")
    if os.path.exists(exe_ico) and not os.path.exists(backup):
        os.replace(exe_ico, backup)
        print("backup ->", backup)
    with open(exe_ico, "wb") as f:
        f.write(open(os.path.join(OUT, "PerfOverlay-light.ico"), "rb").read())
    print("wrote", exe_ico, os.path.getsize(exe_ico), "bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
