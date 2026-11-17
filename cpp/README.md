# TaskbarTempCpp（C++ 原生版 v0.1）

TaskbarTemp 的原生 C++/Win32 重写。目标：常驻内存个位数 MB（.NET 版约 60-70MB）。

## 数据源

| 数据 | 方式 |
|---|---|
| CPU 温度 | PawnIO 驱动 + IntelMSR 模块：MSR 0x19C 热状态（每核，bit31 有效、bits22:16 距 TjMax 差值），TjMax 来自 MSR 0x1A2，取各核最大值 |
| CPU 频率 | APERF/MPERF（MSR 0xE8/0xE7，选中核心的差值）× 基频（MSR 0xCE 非睿频倍频 × 100MHz，回退 CPUID 品牌串） |
| CPU 负载 | GetSystemTimes 差值 |
| 内存 | GlobalMemoryStatusEx |
| 网速 | GetIfTable2 物理适配器 In/OutOctets 差值（排除 WAN Miniport / Wi-Fi Direct / NDIS 过滤器伪接口） |
| GPU 负载和共享显存 | DXGI 枚举适配器，D3DKMT 读取首个 3D 引擎和共享显存统计 |

IntelMSR.bin（PawnIO 模块）从 LibreHardwareMonitor 仓库 `LibreHardwareMonitorLib/Resources/PawnIo/` 取得，以 RCDATA 资源内嵌进 exe。

## 构建

需要 MSVC BuildTools（安装于 `D:\Dev\VSBuildTools`，含 VC++ 工具链 + Windows SDK）：

```
build.cmd
```

产物：`bin\TaskbarTempCpp.exe`（单文件，~70KB + 内嵌模块）。

## 运行

- 需要管理员权限（manifest 声明；PawnIO 设备 ACL 仅允许管理员）
- 需要 PawnIO 驱动（已随系统安装，`C:\Program Files\PawnIO`）
- 托盘图标显示 CPU 温度；悬停查看 CPU、内存和网速；左键打开详情，右键可打开详情、手动刷新或复制温度
- 日志：`%LOCALAPPDATA%\TaskbarTempCpp\run.log`

## 健壮性设计

- 双路径设备打开：`\\.\PawnIO`（新实例）与 `\\?\GLOBALROOT\Device\PawnIO`（旧实例）都尝试
- 设备失联后会重试打开；MSR 读取连续 5 轮全失败时回收句柄并重开。驱动被移除等情况不保证自动恢复。
- 托盘尚未就绪或资源管理器重启时会重试添加图标；开机自启在登录后延迟两分钟。
- 与 C# 版共用单实例互斥体：`Global\TaskbarTemp_SingleInstance`，避免两个版本同时常驻。

## 已知限制（v0.1）

- 详情窗口已按本地 C# 版重做三张卡片，仍需完成深色主题、高 DPI、多屏及细节验收。
- GPU 3D 负载和共享显存已在本机短时读取，尚未与 C# 版在同一时间持续对照；GPU 温度尚未实现。
- 托盘支持单色、彩色数字、胶囊和圆环四种样式；样式修改后需重启。开机自启菜单已迁移，实际注册和移除仍需在部署时验收。
- 暂无正式安装包；原生版与 C# 版共用单实例锁，日志目录独立。
