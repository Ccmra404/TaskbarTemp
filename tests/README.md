# NPU 采样回归测试

在 Windows 上使用 .NET 10 SDK 运行：

```powershell
./tests/Run-NpuRegression.ps1
```

如果 SDK 不在 PATH 中，使用 `-DotnetPath <dotnet.exe 的路径>`。测试使用已有的 NuGet 缓存，在临时副本中模拟硬件与 NPU 查询，不访问真实硬件。它检查查询卡住时的其他采样、单次查询、窗口重开、异常重试和退出。源码结构发生变化时，脚本会拒绝继续执行，需要同步更新测试。

安装包自启归属逻辑可用 `./tests/Test-StartupOwnership.ps1` 检查。该测试模拟计划任务，不会修改系统中的实际任务，并拒绝覆盖其他目录里的同名程序任务。

安装结束后的启动权限可用 `./tests/Test-InstallerLaunchPrivilege.ps1` 检查程序清单和安装器配置是否一致。

安装与卸载辅助脚本的受信任位置可用 `./tests/Test-InstallerTrustBoundary.ps1` 检查；它确认管理员运行的安装和卸载流程不会从用户可写的应用目录加载脚本。

`Test-InstallerUninstallTrustBoundary.ps1 -InstallerPath <安装包>` 会在临时的可写安装目录真实安装并卸载安装包、创建并移除临时自启任务，在应用目录放置恶意同名脚本，确认卸载器不会运行它。该集成测试会写入 Program Files 和计划任务，仅应在一次性 CI 环境运行。
