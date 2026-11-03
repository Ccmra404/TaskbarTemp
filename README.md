# TaskbarTemp

Windows 11 taskbar tray monitor for CPU temperature, CPU/GPU load, shared GPU memory, physical memory, network traffic, and Intel NPU usage.

## Features

- Shows the current CPU temperature in the notification area.
- Hover text and a compact, translucent details window.
- Reads GPU, memory, network, and NPU counters where the hardware and driver expose them.
- Optional startup task and selectable tray icon styles.

Some readings require administrator privileges. NPU usage stays at 0% while no application is running an NPU workload.

## Build

Requirements: Windows x64 and the .NET 10 SDK. Build-time DLL references are kept in `lib/`; `System.Management` is restored from NuGet.

```powershell
dotnet publish .\src\TaskbarTemp.csproj -c Release -r win-x64 --self-contained false -o .\publish\win-x64
```

The published application is a folder containing `TaskbarTemp.exe` and its runtime dependencies, not a standalone single-file executable. Running it requires the .NET 10 Desktop Runtime.

## Windows 安装包

在 Windows x64 上安装 .NET 10 SDK 和 Inno Setup 7，然后执行：

```powershell
.\installer\Build-Installer.ps1 -InnoCompilerPath 'C:\Program Files\Inno Setup 7\ISCC.exe'
```

安装包生成在 `artifacts/installer/`。安装时可选择目录、桌面快捷方式及开机自启；选择自启会更新已有的 TaskbarTemp 启动任务。程序运行需要管理员权限；安装包自带 .NET 运行时，但不会自动安装 PawnIO 硬件驱动。CPU 温度读不到时，请检查驱动和设备支持情况。

安装或卸载不会删除已有的 `config.ini`，不同安装目录之间不会自动迁移配置。卸载时，仅当 TaskbarTemp 开机任务指向被卸载的程序时才移除该任务。

## Configuration

The application creates `config.ini` beside the executable when needed. It is machine-specific and intentionally excluded from Git.

## Repository contents

- `src/` — application source and project file.
- `src/TaskbarTemp.csproj` — pinned package dependencies.
- `lib/` — pinned hardware-monitoring DLL dependencies.
- `licenses/` and `THIRD-PARTY-NOTICES.md` — dependency license notices.
- `README.local.md` — local notes, ignored by Git and not part of the repository.

TaskbarTemp itself does not yet declare a software license; public visibility does not grant reuse rights.
