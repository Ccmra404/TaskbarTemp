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

## Configuration

The application creates `config.ini` beside the executable when needed. It is machine-specific and intentionally excluded from Git.

## Repository contents

- `src/` — application source and project file.
- `src/TaskbarTemp.csproj` — pinned package dependencies.
- `lib/` — pinned hardware-monitoring DLL dependencies.
- `licenses/` and `THIRD-PARTY-NOTICES.md` — dependency license notices.
- `README.local.md` — local notes, ignored by Git and not part of the repository.

TaskbarTemp itself does not yet declare a software license; public visibility does not grant reuse rights.
