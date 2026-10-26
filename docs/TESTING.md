# Testing

## Automated (runs in CI on Windows, macOS, Ubuntu and a Fedora container)
- `Guardian.Core.Tests`: modes and consent, operation gate matrix, cleanup safeguards (age, exclusions, links, ownership,
  in-use, outside-root, user exclusions), holding area move/restore/no-overwrite/changed-file/purge, retry guard,
  repair coordinator (mode, approval fingerprint, incomplete plan, wrong OS, elevation, preflight, verification,
  retry limit), reporting language, read-only runner allow-list.
- `Guardian.Data.Tests`: SQLite persistence of mode, consent, operations and check history.
- `Guardian.Platform.Tests`: parsers for all three OSes with sample output, schedule rendering, Windows repair plans,
  Linux adapter command discipline, real ownership/in-use detection on Linux.
- `Guardian.App.Tests`: headless UI opens in Observe Only, file moves disabled, summary carries the no-guarantee wording.
  Set `GUARDIAN_SCREENSHOT=/path/shot.png` to save screenshots.

Destructive operations are always mocked (`MockExecutor`) or confined to a temporary directory the test creates.
**Never run destructive tests on your primary laptop.**

## Non-interference (disposable machines only)
`tests/noninterference/linux-deb.sh` installs the .deb, runs a report, uninstalls, and diffs packages, `/etc`,
systemd units, crontab, `$HOME`, `/usr` and `/opt`. It refuses to run unless `GUARDIAN_DISPOSABLE_VM=1`.

Before declaring any platform supported, repeat on a clean VM per OS version with these installed and in use:
existing antivirus (Defender plus one third-party on Windows), Chrome/Firefox/Edge/Safari with signed-in profiles,
Microsoft Office or LibreOffice, a development toolchain (VS Code, Git, Python, Node, .NET), third-party firewall and
backup software, several startup apps, and a folder of user documents. Check before and after each of: install,
first run in each mode, cleanup + restore, update to a newer build, uninstall, an install interrupted halfway,
and a repair whose command fails. Record snapshots (Windows: installed programs, services, scheduled tasks, Run keys,
`%ProgramData%`, user profile hashes; macOS: `/Applications`, LaunchAgents/Daemons, `pkgutil --pkgs`, profile hashes)
and investigate every difference.
