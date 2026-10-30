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

## .NET Framework / WPF / Windows Forms

应用程序基于 .NET Framework 4.8 编写，运行时随 Windows 提供，**不随本项目分发**，
因此不涉及再分发义务。本项目不依赖任何第三方 NuGet 运行时包
（`Microsoft.NETFramework.ReferenceAssemblies` 仅在编译期用作引用程序集）。
