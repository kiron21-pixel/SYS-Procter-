# Platform status

A platform is **supported** only after its installer, security integrations, permissions, cleanup safeguards
and uninstall have been verified on that platform (master prompt §9). Nothing has reached that bar yet.

Last updated: 2026-10-26, version 0.1.0.

| | Windows 10 22H2 / 11 | macOS 15+ | Ubuntu 22.04/24.04/26.04 | Debian 12/13 | Fedora 42+ |
|---|---|---|---|---|---|
| Code compiles | ✅ (cross-compiled on Linux) | ✅ (cross-compiled on Linux) | ✅ | ✅ | ✅ |
| Adapter has run on the real OS | ❌ | ❌ | ✅ Ubuntu 24.04 container | ❌ | ❌ |
| Output parsers unit-tested | ✅ with sample output | ✅ with sample output | ✅ | ✅ (shared apt) | ✅ (sample output) |
| Desktop UI rendered | ❌ | ❌ | ✅ headless (Skia) on Ubuntu 24.04 | ❌ | ❌ |
| Installer built | ❌ (WiX source + script only) | ❌ (pkg script only) | ✅ .deb (amd64) | ⚠️ same .deb, not installed on Debian | ❌ (spec only) |
| Install / uninstall non-interference test | ❌ | ❌ | ✅ in an Ubuntu 24.04 container (not a VM) | ❌ | ❌ |
| Code signing | ❌ needs certificate | ❌ needs Developer ID + notarization | n/a | n/a | n/a |
| **Declared supported** | **No** | **No** | **No** (preview) | **No** | **No** |

## How 0.1.0 was verified

- 79 automated tests on Ubuntu 24.04 (x64, .NET 10.0.113): core safety (modes, gate, cleanup safeguards, holding area,
  restore, purge, retry guard, repair approval/fingerprint/preflight), SQLite store, all three adapters' parsers,
  "Fedora never runs apt / Ubuntu never runs dnf", a check that every Linux command issued is read-only, and a
  headless UI test that confirms the app opens in Observe Only with file moves disabled.
- `ai-laptop-guardian --report` run against the real Ubuntu 24.04 container.
- `.deb` installed, run and removed in that container with before/after snapshots of packages, `/etc`, systemd units,
  crontab, `$HOME`, `/usr` and `/opt`: no differences.

## To reach "supported" on each platform

1. Push to GitHub so `.github/workflows/ci.yml` builds and tests on Windows, macOS, Ubuntu and a Fedora container.
2. Run the adapter and UI on a clean VM of each OS version in the table, as a standard user and as admin.
3. Build, sign (Windows Authenticode; Apple Developer ID + notarization) and install the real installer on those VMs.
4. Run the non-interference checklist in [TESTING.md](TESTING.md) with snapshots before and after.
5. Update this table with the date, OS build, and evidence for each tick.
