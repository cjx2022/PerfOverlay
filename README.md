<p align="center">
  <img src="https://testingcf.jsdelivr.net/gh/cjx2022/PerfOverlay@main/docs/logo.png" width="96" height="96" alt="PerfOverlay">
</p>

<h1 align="center">PerfOverlay · 性能浮窗</h1>

<p align="center">
  一行读数，常驻桌面右上角。透明背景、无边框、不挡操作。
</p>

<p align="center">
  <b>开发语言</b> C# · WPF · .NET Framework 4.8 &nbsp;|&nbsp; <b>平台</b> Windows 10 1809+ / 11 · x64 &nbsp;|&nbsp; <b>体积</b> 单文件 1.3 MB &nbsp;|&nbsp; <b>版本</b> v1.0.1 &nbsp;|&nbsp; <b>许可</b> MIT
</p>

---

![PerfOverlay 控制面板与浮窗](https://testingcf.jsdelivr.net/gh/cjx2022/PerfOverlay@main/docs/screenshot.png)

> 左：控制面板。右上：深色背景上的浮窗。右下：浅色背景上的浮窗。

## 功能

| 指标 | 默认 | 说明 |
|---|---|---|
| CPU 利用率 | 关 | 整机占用，内核计数器 |
| 内存利用率 | 关 | 整机占用，悬停看已用 / 总量 |
| 帧数 FPS | **开** | 前台程序的呈现帧率，1 秒窗口 |
| 1% Low | **开** | 最近 10 秒最慢 1% 帧的平均帧率 |
| FT 帧时 | 关 | 相邻呈现间隔的中位数，毫秒 |
| 显存利用率 | 关 | 独占显存，与任务管理器同源 |
| GPU 利用率 | 关 | 整卡利用率，NVIDIA / AMD / Intel 通用 |
| CT | 关 | CPU 温度，寄存器优先、ACPI 兜底 |
| GT | 关 | GPU 温度，零驱动零权限 |
| 时间 | 关 | 时:分，固定最右 |

首次启动只开「帧数 FPS」与「1% Low」，其余关闭 —— 默认就是很短一条。未勾选的指标不打开计数器、不占句柄。

- **外观**：主题 浅色 / 深色 / 跟随系统（即时生效）· 字号 小 / 中 / 大 · 文字色六种 · 描边 · 不透明度 30–100% · 刷新 0.25–2 s
- **交互**：拖动换位置（会记住）· 双击开面板 · 右键菜单（面板 / 隐藏 / 退出）· 托盘双击开面板
- **行为**：锁定位置 · 鼠标穿透 · 开机自启 · 显示托盘图标 · 复位到右上角

<p align="center">
  <img src="https://testingcf.jsdelivr.net/gh/cjx2022/PerfOverlay@main/docs/console-dark.png" width="620" alt="深色主题的控制面板">
</p>

## 指标解释

- **FPS**：1 秒内该程序的呈现次数 ÷ 实际时长，至少 4 帧才出数。
- **FT 帧时**：同一窗口内相邻 `Present` 间隔的**中位数**。等于帧率倒数：16.7 ms = 60 FPS，6.9 ms = 144 FPS。中位数不会被单帧卡顿拉飞，代表「平常每帧间隔多久」；最慢 5% 帧在悬停里。
- **1% Low**：最近 10 秒的帧时间从大到小取最慢 1%，求这些帧的平均帧时间再取倒数，与 CapFrameX / MSI Afterburner 同口径。少于 100 帧不出数。
- **显存**：`\GPU Adapter Memory(*)\Dedicated Usage` 按 LUID 匹配到的那块显卡，与任务管理器一致。
- **GPU**：`\GPU Engine` 取最忙引擎，与任务管理器同源。
- **CT / GT**：CT 装了温度组件就读 die 寄存器，否则退固件 ACPI 热区；GT 走 WDDM 适配器性能数据。
- 多 swapchain 只取最密集的一条，同一帧不会算两遍；Electron / Chromium 系按进程名回落到 GPU 子进程，不会因为主进程不提交画面而变 `N/A`。

## 已知限制

- **独占全屏不可见**：本程序非注入式，不挂钩游戏画面。独占全屏下 Windows 不合成叠加窗口，浮窗无法显示，改用无边框窗口 / 窗口化全屏。
- **FT 是帧间隔，不是出屏延迟**：不含渲染排队、合成、扫描与面板响应，不能当输入延迟或端到端延迟用。
- **反作弊 / 安全软件**可能拦截 ETW，帧数不可用。界面会直说「采集被拦截」，退出游戏后自动恢复，也可在面板点「恢复」重建。
- **前台不提交画面时显示 `N/A`**：暂停、最小化、静态桌面属预期，不是采集失败。
- **多显卡**取物理显存最大的一块，不做逐卡切换。
- **只定位主显示器**，多屏拓展时浮窗不会跑到副屏。
- **读 CT 需要管理员**：PawnIO 设备只授权 SYSTEM 与 Administrators。拒绝提权时 CT 显示 `--`，其它指标不受影响。
- **ACPI 占位值**：主板给恒定读数时判为死值并自动切另一路；只有 ACPI 可读且读数不动时，CT 旁出现感叹号 —— 意思是「这个数不可信」，不是「没装驱动」。

## 下载与使用

1. 到 [Releases](https://github.com/cjx2022/PerfOverlay/releases/latest) 下载 `PerfOverlay-1.0.1.exe`。
2. 双击运行。首次启动请求一次管理员权限（采集帧数用）。
3. 浮窗出现在右上角，双击它或点托盘图标打开控制面板。

配置在 `%APPDATA%\PerfOverlay\settings.json`，采集器释放到 `%LOCALAPPDATA%\PerfOverlay\bin\<版本>\`；程序目录下有 `tools\PresentMon.exe` 时优先用它。卸载删这两处和 exe 即可。

**兼容性**　高 DPI 按 PerMonitorV2 感知，125% / 150% 下贴角与字号一致 · 多显示器按主显示器工作区定位，自动避开任务栏 · 免安装，不写系统目录、不装服务

**权限**　帧数依赖 ETW 的 DXGI 事件。拒绝提权时程序照常运行，CPU / 内存 / 显存可用，帧数与 1% Low 显示 `N/A`，不会伪造数据。账号已在本地 **Performance Log Users** 组的可免提权采集。

## 开源组件

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| [PresentMon](https://github.com/GameTechDev/PresentMon) | 2.6.0 | MIT（Intel Corporation） | 读取 GPU 呈现事件，计算帧数与 1% Low |
| [PawnIO 模块 AMDFamily17](https://github.com/namazso/PawnIO.Modules) | 0.2.11 | LGPL-2.1 | 读 AMD SMN 寄存器取 CPU 温度（可选） |
| [PawnIO 模块 IntelMSR](https://github.com/namazso/PawnIO.Modules) | 0.2.11 | LGPL-2.1 | 读 Intel MSR 温度寄存器取 CPU 温度（可选） |

三者都以嵌入资源打包进 exe，版权与许可全文见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。PawnIO 驱动本体不由本项目分发，只在你点「下载并安装」后从官方获取。除此之外只用到 .NET Framework 自带的 WPF / Windows Forms / System.Drawing，无第三方 NuGet 运行时依赖。

## 许可

[MIT](LICENSE) © 2026 BillChen · 更新记录见 [Releases](https://github.com/cjx2022/PerfOverlay/releases)
