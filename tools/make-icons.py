#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""生成程序图标（纯程序化，不依赖任何外部图片）。

产物：
  assets/PerfOverlay-light.ico  给浅色任务栏用（蓝底，与设计稿逐像素一致）
  assets/PerfOverlay-dark.ico   给深色任务栏用（近黑底 + 一圈细白框）
  app.ico                       exe 主图标（= 浅色版）

用法：python tools/make-icons.py
"""
import os
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "assets")
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# ----------------------------------------------------------------------
# 图标：圆角色块 + 从左下升到右上的白折线
#
# 早期用过 AI 出的设计稿（浅蓝卡片 + 网格 + 折线 + 数字标注），256 上好看，
# 但缩到 16/24 就是一片灰：网格和数字占掉大半像素，折线只剩 1px。任务栏显示
# 的恰恰是 16~24，那一版等于白做。所以改成程序化绘制，三步保证小尺寸清楚：
#   1. 折线跨度拉满 —— 起点贴左下、终点贴右上，斜率够大才看得出趋势；
#   2. 折点全尺寸同一套 —— 托盘与文件图标必须是同一条线，不按尺寸简化；
#   3. 抗锯齿遮罩做硬化 —— 下采样后的半透明边不发灰，线宽不会被啃细。
# ----------------------------------------------------------------------
BG_FOR_LIGHT_TASKBAR = (25, 102, 210)    # #1966D2，压在浅灰任务栏上对比 6:1
BG_FOR_DARK_TASKBAR = (14, 17, 22)       # #0E1116 近黑，深色任务栏上的「黑加白」
WHITE = (255, 255, 255)
SUPERSAMPLE = 8                          # 16px 也是 128×128 画，边缘才够平

# 不加描边，两版都不加。
#
# 试过：`rounded_rectangle(outline=…, width=…)` 是沿圆角路径**向内**描边，
# 20px 上「1px 框」等于吃掉 5% 的块、把折线的边距也压掉一圈 —— 那不是「加个框」，
# 是换了个 logo；而且 24px 帧缩到 20px 后白框只剩一圈 50% 灰，比不加更脏。
# 结论：不加。浅色版 ico 与设计稿 sha256 逐字节一致。

# 折线：全尺寸共用一套 4 个折点，不做「小尺寸简化」。
#
# 之前按尺寸分三档（16/24 三点、32/48 四点、64+ 六点），看着像照顾小尺寸，
# 实际是同一个 logo 在不同地方形状不一样：托盘 20px 取 24 帧（三点，中段平走），
# 文件图标 20px 是 32/48 帧缩下来的（四点，中段带一个下凹）——
# 实测两张 20px 图中心线平均差 1.09px，肉眼就是「不是同一条线」。
#
# 现在统一用下面这套四点（原 32/48 档的几何），实测与参考文件的 20px 中心线
# 偏差 0.16px，而三点档 1.09px、六点档 1.16px —— 这就是「文件图标」那条线。
#
# 中间折点必须**离开首尾连线**，否则退化成斜杠：第二点在首尾连线上方、
# 第三点在下方，形成 上→下凹→陡升 的折线，缩到 16px 也认得出是折线。
# 端点内边距 0.145/0.855：线宽一半约 0.065，贴太紧会被圆角啃掉。
LINE = [(0.145, 0.855), (0.360, 0.600), (0.580, 0.700), (0.855, 0.145)]


def harden(mask):
    """把抗锯齿的软边收紧：覆盖度 >0.66 判纯白、<0.34 判无，中间线性。

    不收紧的话，白线在蓝底上下采样出来那圈半透明像素会显示成灰色，
    16px 上等于给线包了一层灰边，宽度也被啃掉三分之一。
    """
    lut = []
    for i in range(256):
        v = (i / 255.0 - 0.34) / 0.32
        v = 0.0 if v < 0 else (1.0 if v > 1 else v)
        lut.append(int(round(v * 255)))
    return mask.point(lut)


def render(size, bg):
    """画一张 size×size 的图标：圆角底块 + 白色折线。"""
    big = size * SUPERSAMPLE
    radius = int(round(big * 0.22))

    # 底块在超采样尺度上画：直接按目标尺寸画圆角，16px 下会缺角
    # （PIL 的 rounded_rectangle 在半径小于 3px 时角部不精确）。
    base = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(base).rounded_rectangle(
        [0, 0, big - 1, big - 1], radius=radius, fill=bg + (255,))
    im = base.resize((size, size), Image.LANCZOS)

    # 前景单独画在透明层上再叠底：直接在底色上画，下采样的半透明边
    # 会和蓝底混出一圈灰，16px 上特别明显。
    fg = Image.new("L", (big, big), 0)
    fd = ImageDraw.Draw(fg)
    pts = [(x * big, y * big) for x, y in LINE]
    # 线宽下限 2.1px：16px 再细就只剩一条虚影；小尺寸再抬一档，
    # 因为硬化会啃掉边缘半圈，实际视觉宽度比设定值窄。
    coef = 0.130 if size <= 24 else 0.115
    width = max(2.1, size * coef) * SUPERSAMPLE
    fd.line(pts, fill=255, width=int(round(width)), joint="curve")
    for p in (pts[0], pts[-1]):          # 线头补圆，避免端点切成斜角
        rr = width / 2.0
        fd.ellipse([p[0] - rr, p[1] - rr, p[0] + rr, p[1] + rr], fill=255)

    fg = harden(fg.resize((size, size), Image.LANCZOS))

    im.paste(Image.new("RGBA", (size, size), WHITE + (255,)), (0, 0), fg)
    return im


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    variants = {
        # 名字是「给哪套主题用」，不是图标颜色
        "light": BG_FOR_LIGHT_TASKBAR,
        "dark": BG_FOR_DARK_TASKBAR,
    }
    for k, bg in variants.items():
        frames = {s: render(s, bg) for s in ICO_SIZES}
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
