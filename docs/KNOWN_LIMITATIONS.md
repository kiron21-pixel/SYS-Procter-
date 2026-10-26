# Unsupported capabilities and known limitations (0.1.0)

## Everywhere
- **Preview only.** No platform is declared supported; see [PLATFORM_STATUS.md](PLATFORM_STATUS.md).
- No antivirus scanning, real-time protection, or malware removal. Guardian reports on the OS's own protection.
- No AI features yet, despite the name.
- Scheduled reports: definitions for Task Scheduler, launchd and systemd timers are generated in code, but the UI
  does not install them yet.
- Controlled Repair: the plan, approval, preflight and execution engine exist and are tested with mocks. The UI does
  not yet offer repair proposals, so enabling Mode C currently unlocks nothing extra.
- Self-update is not implemented. Update through your installer or package manager.
- High CPU per process is not measured (memory only).
- The app has not been checked with screen readers.

## Windows
- Defender status comes from `Get-MpComputerStatus`. When another antivirus is active, Defender may report as off; Guardian
  says so rather than calling it a problem.
- The Security Center `productState` value is undocumented by Microsoft. Guardian's decoding is the widely used one and is labelled best-effort.
- "Recent Windows updates" reads install history only. It does not ask Windows Update whether new updates are waiting.
- BitLocker, DISM and SFC checks need administrator rights and report "Permission required" otherwise. Windows Home's
  Device Encryption is not detected.
- Startup inventory covers Run keys and Startup folders, not scheduled tasks or services. Microsoft Store apps are not listed.
- In-use detection opens the file exclusively for a moment. A program that opens files only intermittently might not be detected at that instant; the 7-day age rule is the second safeguard.

## macOS
- Login Items (System Settings > General) need administrator rights to read and are not listed.
- Restart-pending detection is not implemented.
- Memory "available" is approximate (macOS uses spare memory for caches and compression).
- `softwareupdate --list` contacts Apple and can take minutes, so it runs on demand only.
- Guardian does not and will not try to change Gatekeeper, SIP, XProtect, the firewall or FileVault.
- The app bundle has not been signed or notarized yet; Gatekeeper will block an unsigned build.

## Linux
- Supported package managers: apt (Ubuntu, Debian and derivatives that declare `ID_LIKE=debian/ubuntu`) and dnf (Fedora).
  Others (Arch, openSUSE, …) get "Unsupported" for package checks.
- Pending updates come from local package metadata (`apt-get -s`, `dnf --cacheonly`), which may be stale.
- Reading ufw rules needs root; without it Guardian only reports whether the ufw service is running.
- Firewalls configured directly with nftables/iptables can't be fully assessed.
- In-use detection reads `/proc/*/fd` for processes Guardian can see. Without root, other users' processes are not
  visible, so a file in shared `/tmp` that another user's program has open would not be detected. Ownership and the
  7-day rule still apply. Run as root or keep `$TMPDIR` per-user to close this gap.
- Disk encryption detection recognises dm-crypt/LUKS only.
- Flatpak and Snap apps are listed; AppImages are not.
