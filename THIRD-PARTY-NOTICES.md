# Third-party notices

This repository includes the following pinned DLL dependencies in `lib/`.

| Component | Version | License | Notice |
|---|---:|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | [`licenses/LibreHardwareMonitor/LICENSE`](licenses/LibreHardwareMonitor/LICENSE) and [`THIRD-PARTY-NOTICES.txt`](licenses/LibreHardwareMonitor/THIRD-PARTY-NOTICES.txt) |
| DiskInfoToolkit | 2.1.3 | MPL-2.0 | [`licenses/LibreHardwareMonitor/LICENSE`](licenses/LibreHardwareMonitor/LICENSE) |
| RAMSPDToolkit-NDD | 1.6.0 | MPL-2.0 | [`licenses/LibreHardwareMonitor/LICENSE`](licenses/LibreHardwareMonitor/LICENSE) |
| HidSharp | 2.6.4 | Apache-2.0 | [`licenses/HidSharp/Apache-2.0.txt`](licenses/HidSharp/Apache-2.0.txt) |
| Mono.Posix.NETStandard | 1.0.0 | MIT | [`licenses/Mono.Posix/MIT.txt`](licenses/Mono.Posix/MIT.txt) |
| System.IO.Ports | .NET 10 | MIT | [`licenses/dotnet/MIT.txt`](licenses/dotnet/MIT.txt) |

The LibreHardwareMonitor third-party notice also contains the LGPL-2.1 notice for its embedded PawnIO modules.

The experimental native build embeds `cpp/IntelMSR.bin`, a PawnIO.Modules Intel MSR module. Its upstream source is [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules), licensed LGPL-2.1-or-later; the license text is included in the LibreHardwareMonitor third-party notice above. The included binary has SHA-256 `D6ED85D65AB17A22F813EF98207D6D537155EE2DED5976A21CB48413C9B92E5F`.

The native hardware icons are adapted from [Lucide](https://github.com/lucide-icons/lucide). Its ISC and applicable Feather MIT license notices are in [`licenses/Lucide/LICENSE`](licenses/Lucide/LICENSE).
