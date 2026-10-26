# Privacy policy (draft for 0.1.0)

**Short version: everything stays on your computer. Guardian has no servers, accounts, telemetry or analytics.**

## What Guardian reads
- Operating system name, version, architecture and whether it runs with administrator rights.
- Status of built-in security features (antivirus, firewall, updates, disk encryption, SIP/Gatekeeper, etc.).
- Memory and disk usage, and the names and memory use of running programs.
- Names and locations of startup items and installed applications.
- File names, sizes, dates and owners in your temporary folder (not file contents).

## What Guardian stores
In its own folder only:
- Windows: `%LOCALAPPDATA%\AI Laptop Guardian`
- macOS: `~/Library/Application Support/AI Laptop Guardian`
- Linux: `~/.local/share/ai-laptop-guardian` (or `$XDG_DATA_HOME`)

That folder holds `guardian.db` (SQLite: mode and consent history, operation log, check history) and `holding/`
(temp files you chose to move, kept 7 days so you can restore them).

## What Guardian sends
Nothing. Guardian makes no network connections itself. Some OS tools it calls may contact their vendor as part of
their normal job (for example macOS `softwareupdate --list` contacts Apple); those checks are on demand and labelled.

## Deleting your data
Uninstalling leaves the data folder in place so your logs and held files are not lost by accident. Delete the folder
above to remove everything. Before you do, restore anything you still want from the holding area.

## Build-time note for developers
The Avalonia build tools include optional anonymous build telemetry. CI sets `AVALONIA_TELEMETRY_OPTOUT=1`; set it
locally too if you prefer. This affects developer machines only, never the installed app.
