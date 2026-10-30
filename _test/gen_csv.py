#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成「已知答案」的 PresentMon CSV，用来校验 PerfOverlay 的解析与 1% Low 聚合。

每个文件头两行写死期望值，PerfOverlay --replay 会自己比对：
    # expect fps=60.00 low=60.00 frames=600
    # desc 说明

注意 1% Low 的口径：最近 10 秒内所有帧的帧时间降序，取最差 1%（向上取整、至少 1 帧），
求这些帧的平均帧时间，再 1000/平均。所以「期望值」必须按这个口径手算出来。
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "csv")

# PresentMon --v1_metrics 的列（关键列都在，解析器按列名取，不依赖顺序）
HEADER = ("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,"
          "AllowsTearing,PresentMode,msInPresentAPI,msBetweenPresents,msUntilRenderComplete,"
          "msUntilDisplayed,msBetweenDisplayChange,msUntilNextPresent,TimeInSeconds")

PID = 4321
APP = "test3d.exe"


def row(t, ms, chain="0x0000000000001234", pid=PID, app=APP):
    """ms 为 None 时输出空串，模拟 CSV 缺列值的情况。"""
    m = "" if ms is None else ("%.6f" % ms)
    return "%s,%d,%s,Other,0,0,0,Hardware: Legacy Flip,0.4,%s,1.0,8.0,%.6f,0.1,%.6f" % (
        app, pid, chain, m, ms if ms else 0.0, t)


def write(name, lines, expect, desc):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# expect " + expect + "\n")
        f.write("# desc " + desc + "\n")
        f.write(HEADER + "\n")
        for ln in lines:
            f.write(ln + "\n")
    print("%-30s %6d 行  %s" % (name, len(lines), expect))


# ----------------------------------------------------------------------
# 1) 稳定 60fps：FPS 与 1% Low 都应当是 60
# ----------------------------------------------------------------------
def case_stable():
    dt = 1000.0 / 60.0
    lines, t = [], 0.0
    for _ in range(600):
        lines.append(row(t, dt))
        t += dt / 1000.0
    write("01-stable-60fps.csv", lines,
          "fps=60.00 low=60.00 frames=600",
          "600 帧稳定 60fps")


# ----------------------------------------------------------------------
# 2) 6 次 100ms 尖刺：最差 1%（600 帧的 1% = 6 帧）恰好就是这 6 帧
#    → 平均帧时间 100ms → 10.00 fps
#    尖刺放在第 1 秒附近，确保落在 10 秒窗口内、又不在最后 1 秒（FPS 仍是 60）
# ----------------------------------------------------------------------
def case_spikes():
    dt = 1000.0 / 60.0
    lines, t = [], 0.0
    for i in range(600):
        ms = 100.0 if 60 <= i < 66 else dt
        lines.append(row(t, ms))
        t += ms / 1000.0
    write("02-six-100ms-spikes.csv", lines,
          "fps=60.00 low=10.00",
          "594 帧 60fps + 6 帧 100ms → 1%Low 应塌到 10")


# ----------------------------------------------------------------------
# 3) 同进程两条 swapchain（帧生成/覆盖层常见）：同一批帧重复上报
#    必须按 swapchain 去重，否则 FPS 会翻倍成 120
# ----------------------------------------------------------------------
def case_dup_chain():
    dt = 1000.0 / 60.0
    a, b, t = [], [], 0.0
    for _ in range(600):
        a.append(row(t, dt, chain="0x000000000000AAAA"))
        b.append(row(t, dt, chain="0x000000000000BBBB"))
        t += dt / 1000.0
    write("03-dup-swapchain.csv", a + b,
          "fps=60.00 low=60.00",
          "两条 swapchain 重复上报 → 必须取记录多的那条，不能翻倍成 120")


# ----------------------------------------------------------------------
# 4) msBetweenPresents 缺失（空列）：只能靠 TimeInSeconds 相邻差回填
#    首帧没有参考值，丢弃；其余 599 帧应当还原成 60fps
# ----------------------------------------------------------------------
def case_missing_ms():
    dt = 1000.0 / 60.0
    lines, t = [], 0.0
    for _ in range(600):
        lines.append(row(t, None))
        t += dt / 1000.0
    write("04-missing-ms-between.csv", lines,
          "fps=60.00 low=60.00",
          "msBetweenPresents 全空 → 用 TimeInSeconds 差值回填")


# ----------------------------------------------------------------------
# 5) 高频但周期卡顿：FPS 看着很高，1% Low 才暴露问题（这正是要看它的理由）
#    1200 帧 120fps，每 100 帧是 40ms 卡顿（共 12 次）
#    最差 1%（1200 的 1% = 12 帧）= 这 12 次卡顿 → 平均 40ms → 25.00 fps
# ----------------------------------------------------------------------
def case_bursty():
    dt = 1000.0 / 120.0
    lines, t = [], 0.0
    for i in range(1200):
        ms = 40.0 if i % 100 == 99 else dt
        lines.append(row(t, ms))
        t += ms / 1000.0
    write("05-bursty-stutter.csv", lines,
          "low=25.00",
          "1200 帧的高帧率 + 12 次 40ms 卡顿 → 1%Low 25，FPS 仍很高")


if __name__ == "__main__":
    case_stable()
    case_spikes()
    case_dup_chain()
    case_missing_ms()
    case_bursty()
    print("\n输出目录:", OUT)
