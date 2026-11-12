#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""生成程序图标（纯程序化，不依赖任何外部图片）。

产物：
  assets/PerfOverlay-light.ico  给浅色任务栏用（深蓝底）
  assets/PerfOverlay-dark.ico   给深色任务栏用（亮蓝底）
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
#   2. 折点数随尺寸递减 —— 16px 只画 1 个肘部，64px 以上才画完整波动；
#   3. 抗锯齿遮罩做硬化 —— 下采样后的半透明边不发灰，线宽不会被啃细。
# ----------------------------------------------------------------------
BG_FOR_LIGHT_TASKBAR = (25, 102, 210)    # #1966D2，压在浅灰任务栏上对比 6:1
BG_FOR_DARK_TASKBAR = (14, 17, 22)       # #0E1116 近黑，深色任务栏上的「黑加白」
WHITE = (255, 255, 255)
SUPERSAMPLE = 8                          # 16px 也是 128×128 画，边缘才够平

# 折线端点：左下 (60) -> 右上 (165)。斜度大，缩到 16px 也是一条明确的上升线。
# 折点表按尺寸分组：小尺寸少折点，否则折角糊在一起反而像一根粗斜线。
#
# 中间折点必须**离开首尾连线**，否则三点共线，16px 上就退化成斜杠。
# 小尺寸取「前段近乎平走、后段陡升」：两段夹角 ~45°，肘部法向偏移在 16px 上
# 约 3px（线宽才 2px），所以缩到任务栏也是「折线」而不是「一根斜杠」。
# 端点留 0.155/0.845 的内边距：线宽一半约 0.065，贴太紧会被圆角啃掉。
LINE_SMALL = [(0.135, 0.845), (0.535, 0.735), (0.865, 0.155)]    # 16/24：一个肘部
LINE_MID = [(0.145, 0.855), (0.360, 0.600), (0.580, 0.700), (0.855, 0.145)]  # 32/48
LINE_LARGE = [(0.135, 0.865), (0.300, 0.635), (0.455, 0.690),
              (0.615, 0.455), (0.775, 0.520), (0.865, 0.135)]    # 64 以上：完整波动


def line_points(size):
    if size <= 24:
        return LINE_SMALL
    if size <= 48:
        return LINE_MID
    return LINE_LARGE


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
    """画一张 size×size 的图标。"""
    big = size * SUPERSAMPLE

    # 底块也在超采样尺度上画：直接按目标尺寸画圆角，16px 下会缺角
    # （PIL 的 rounded_rectangle 在半径小于 3px 时角部不精确）。
    base = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(base).rounded_rectangle(
        [0, 0, big - 1, big - 1], radius=int(round(big * 0.22)), fill=bg + (255,))
    base = base.resize((size, size), Image.LANCZOS)

    # 前景单独画在透明层上再叠底：直接在底色上画，下采样的半透明边
    # 会和蓝底混出一圈灰，16px 上特别明显。
    fg = Image.new("L", (big, big), 0)
    fd = ImageDraw.Draw(fg)
    pts = [(x * big, y * big) for x, y in line_points(size)]
    # 线宽下限 2.1px：16px 再细就只剩一条虚影；小尺寸再抬一档，
    # 因为硬化会啃掉边缘半圈，实际视觉宽度比设定值窄。
    coef = 0.130 if size <= 24 else 0.115
    width = max(2.1, size * coef) * SUPERSAMPLE
    fd.line(pts, fill=255, width=int(round(width)), joint="curve")
    for p in (pts[0], pts[-1]):          # 线头补圆，避免端点切成斜角
        rr = width / 2.0
        fd.ellipse([p[0] - rr, p[1] - rr, p[0] + rr, p[1] + rr], fill=255)

    fg = harden(fg.resize((size, size), Image.LANCZOS))

    im = base
    im.paste(Image.new("RGBA", (size, size), WHITE + (255,)), (0, 0), fg)
    return im


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    variants = {
        "light": BG_FOR_LIGHT_TASKBAR,   # 名字是「给哪套任务栏用」，不是图标颜色
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
