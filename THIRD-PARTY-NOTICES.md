# 第三方组件声明 / Third-Party Notices

PerfOverlay 自身以 MIT 许可发布（见 `LICENSE`）。发布用的单个 exe 里嵌入了下列第三方组件，
按各自许可要求在此保留版权与许可声明。

---

## PresentMon 2.6.0

- 用途：采集 GPU 提交的帧事件，用于「帧数 FPS」与「1% Low」两项指标。
- 来源：<https://github.com/GameTechDev/PresentMon>
- 二进制：`PresentMon.exe`，980,320 字节，SHA-256
  `B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF`
- 分发方式：以**嵌入资源**的形式打进 `PerfOverlay.exe`；首次运行时释放到
  `%LOCALAPPDATA%\PerfOverlay\bin\<版本>\PresentMon.exe`，由 PerfOverlay 作为子进程调用。
  这属于再分发，因此下方许可全文随本仓库一并提供。
- 许可：MIT

```
Copyright (C) 2017-2024 Intel Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included
in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL
THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE
OR OTHER DEALINGS IN THE SOFTWARE.
```

---

## PawnIO 模块 `AMDFamily17.bin`（用于 CPU 温度）

- 用途：通过 PawnIO 驱动读 AMD 的 SMN 寄存器（`THM_TCON_CUR_TMP` @ `0x00059800`），
  得到 CPU 封装温度（CT）。只在用户主动安装温度组件、且处理器为 AMD 17h~1Ah 时才会被加载。
- 来源：<https://github.com/namazso/PawnIO.Modules>（模块包 `release_0_2_11.zip`，
  上游项目 <https://github.com/namazso/PawnIO>）
- 二进制：`AMDFamily17.bin`，10,652 字节，SHA-256
  `DAE74615761B78BDF064DFB3E136252DDCC6FC727D88F14738D0E5800D427A91`
- 分发方式：以**嵌入资源**打进 `PerfOverlay.exe`，运行时整块喂给 PawnIO 设备。
  程序目录下的 `tools\AMDFamily17.bin`（或根目录同名文件）**优先于内嵌版本**，
  因此用户可以替换成自己编译的模块，不需要重新编译本程序。
- 许可：**LGPL-2.1**，全文见 `LICENSES/LGPL-2.1-PawnIO-Modules.txt`。
  源码获取：<https://github.com/namazso/PawnIO.Modules>（同名 `AMDFamily17.p`）。

## PawnIO 模块 `IntelMSR.bin`（用于 CPU 温度）

- 用途：通过 PawnIO 驱动读 Intel 的 MSR —— `IA32_TEMPERATURE_TARGET`（`0x1A2`，取 TjMax）
  与 `IA32_PACKAGE_THERM_STATUS`（`0x1B1`，取当前偏移），相减得到 CPU 封装温度（CT）。
  只在用户主动安装温度组件、且处理器为 Intel Core（family 6）时才会被加载。
- 来源：<https://github.com/namazso/PawnIO.Modules>（同一模块包 `release_0_2_11.zip`）
- 二进制：`IntelMSR.bin`，5,324 字节，SHA-256
  `D6ED85D65AB17A22F813EF98207D6D537155EE2DED5976A21CB48413C9B92E5F`
- 分发方式：与 `AMDFamily17.bin` 相同 —— 嵌入资源打进 exe，程序目录下的同名 `.bin` 优先。
- 许可：**LGPL-2.1**，全文见 `LICENSES/LGPL-2.1-PawnIO-Modules.txt`。
  源码获取：<https://github.com/namazso/PawnIO.Modules>（同名 `IntelMSR.p`）。

### 关于 PawnIO 驱动本体

驱动与官方安装器 `PawnIO_setup.exe` **不由本项目分发**：程序只会在用户点击「下载并安装」后
从官方地址取一份，校验数字签名者为 `namazso` 之后交给它自己安装。

驱动为 GPL-2.0（附特殊例外，全文见 <https://github.com/namazso/PawnIO>）。本程序
**不链接 `PawnIOLib.dll`**，只通过 `DeviceIoControl` 与设备通信 —— 这正是该例外明文允许的用法：

> In addition, as a special exception, the copyright holders of PawnIO give you permission
> to combine PawnIO program with free software programs or libraries that are released under
> the GNU LGPL and with independent modules that communicate with PawnIO **solely through
> the device IO control interface**.

因此本项目的 MIT 许可不受影响。

---

## .NET Framework / WPF / Windows Forms

应用程序基于 .NET Framework 4.8 编写，运行时随 Windows 提供，**不随本项目分发**，
因此不涉及再分发义务。本项目不依赖任何第三方 NuGet 运行时包
（`Microsoft.NETFramework.ReferenceAssemblies` 仅在编译期用作引用程序集）。
