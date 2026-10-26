# Dependency and licence inventory (0.1.0)

Versions verified against nuget.org on 2026-10-26. Re-check before each release.

## Runtime (shipped inside the app)
| Component | Version | Licence | Notes |
|---|---|---|---|
| .NET runtime | 10.0 (LTS, supported to Nov 2028) | MIT | Self-contained in installers |
| Avalonia, Avalonia.Desktop/Win32/X11/Native/FreeDesktop/Skia/HarfBuzz, Themes.Fluent, Remote.Protocol | 12.1.4 | MIT | UI framework |
| Avalonia.Fonts.Inter (Inter typeface) | 12.1.4 | MIT (package) / SIL OFL 1.1 (font) | |
| Avalonia.Angle.Windows.Natives (ANGLE) | 2.1.27548 | BSD-3-Clause | Windows only |
| SkiaSharp + native assets | 3.119.4 | MIT (Skia: BSD-3-Clause) | Rendering |
| HarfBuzzSharp + native assets | 8.3.1.3 | MIT (HarfBuzz: Old MIT) | Text shaping |
| MicroCom.Runtime | 0.11.6 | MIT | |
| Tmds.DBus.Protocol | 0.94.1 | MIT | Linux desktop integration |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | |
| Microsoft.Data.Sqlite (+ .Core) | 10.0.14 | MIT | |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider, lib) | 2.1.13 | Apache-2.0 | |
| SQLite | bundled via e_sqlite3 | Public domain | |

## Build and test only (not shipped)
| Component | Version | Licence |
|---|---|---|
| Avalonia.BuildServices | 11.3.2 | MIT (optional anonymous build telemetry; opt out with `AVALONIA_TELEMETRY_OPTOUT=1`) |
| Avalonia.Headless | 12.1.4 | MIT |
| xunit / xunit.runner.visualstudio | 2.9.3 / 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| WiX Toolset | 5.0.2 | MS-RL |

## Operating-system tools Guardian calls (not redistributed)
Windows PowerShell (Defender, NetSecurity, CIM cmdlets), dism.exe, sfc.exe, sc.exe; macOS spctl, csrutil,
socketfilterfw, fdesetup, plutil, softwareupdate, sw_vers, vm_stat, sysctl, system_profiler, lsof; Linux systemctl,
ufw, firewall-cmd, apt-get, dpkg-query, dnf, rpm, needs-restarting, getenforce, lsblk, clamscan, flatpak, snap.

**No third-party security engine is bundled.** ClamAV is only detected if the user already installed it.

## Guardian's own licence
Not chosen yet (see LICENSE). Pick one before distributing.
