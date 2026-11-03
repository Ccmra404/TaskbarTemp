# NPU 采样回归测试

在 Windows 上使用 .NET 10 SDK 运行：

```powershell
./tests/Run-NpuRegression.ps1
```

如果 SDK 不在 PATH 中，使用 `-DotnetPath <dotnet.exe 的路径>`。测试使用已有的 NuGet 缓存，在临时副本中模拟硬件与 NPU 查询，不访问真实硬件。它检查查询卡住时的其他采样、单次查询、窗口重开、异常重试和退出。源码结构发生变化时，脚本会拒绝继续执行，需要同步更新测试。

安装包自启归属逻辑可用 `./tests/Test-StartupOwnership.ps1` 检查。该测试模拟计划任务，不会修改系统中的实际任务。

安装结束后的启动权限可用 `./tests/Test-InstallerLaunchPrivilege.ps1` 检查程序清单和安装器配置是否一致。
