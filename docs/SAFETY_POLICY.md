# Safety policy and where it is enforced

The rule above all others: **when uncertain, observe and report instead of changing the system.**

| Requirement | Enforcement in code |
|---|---|
| Observe Only is the default | `ModeManager.Current` returns `ObserveOnly` when nothing is stored. Tests: `ModeTests.Fresh_install_is_observe_only`, `DatabaseTests.Fresh_database_starts_in_observe_only`, `MainWindowTests`. |
| Explicit consent before maintenance | `ModeManager.RaiseTo` needs the exact typed phrase; consent (user, time, text shown, text typed) is stored in SQLite `mode_history`. Lowering needs no confirmation. |
| Every change goes through a gate | `OperationGate.Check(OperationKind)`. `HoldingAreaCleaner` and `RepairCoordinator` check it before doing anything. |
| Checks cannot change the system | Adapters only get a `ReadOnlyCommandRunner`, which refuses any command not on that adapter's allow-list. Commands run by absolute path, never through a shell. Test: `LinuxTests.Fedora_never_runs_apt_and_ubuntu_never_runs_dnf` asserts every issued command is read-only. |
| No package-manager command on the wrong distro | `OsRelease.PackageManagerFor` returns apt, dnf, or null; commands are only built for the matching one. Unknown distro → "Unsupported". |
| Never delete a file just because it is temporary, old, large or unfamiliar | `CleanupScanner.Evaluate`: inside the per-user temp folder only; not a link/junction anywhere in its path; not on the built-in or user exclusion list (documents, databases, lock files, installers, keys…); unchanged for 7+ days; **owned by the current user**; **not open in any process**. Any "unknown" answer = not eligible. |
| Path validation, ownership, file-use checks, exclusions, operation logs | Same as above, re-run immediately before each move (and the file must be unchanged since the scan). Every move is logged with its exact target and rollback data. |
| Undo where feasible, never claim rollback that does not exist | Temp files are **moved** to Guardian's holding area, not deleted, and `Restore` puts them back (never overwriting). After 7 days `PurgeExpired` deletes them and marks the log entry `Irreversible`. Repairs without an undo are logged `Irreversible` and say so before approval. |
| No endless retries | `RetryGuard`: after 2 failures for the same target in 24 h, Guardian stops trying. |
| Controlled Repair: 10 steps | `RepairPlan` has a field for each step; `RepairPlanValidator` rejects incomplete plans; `RepairApproval` is bound to the plan's SHA-256 fingerprint so approval of one operation can't authorise another; `IRepairPreflight` blocks on pending restarts, running Windows servicing, or low disk; `IRepairExecutor` runs only the plan's exact command and then its verification. |
| Never kill processes | No code path stops another process. The only `Kill` is on Guardian's own child process when a read-only command times out. |
| Never disable security software or change system settings | No adapter contains a command that writes settings. Follow-ups tell the user where to make a change themselves. |
| Honest reporting | `CheckReport` carries implemented / support / permission / ran / evidence / changes / succeeded / follow-up. `ReportLanguage.Summarize` always ends with "This is not a guarantee that the computer is safe." `ReportingTests.No_source_or_ui_text_contains_an_overclaim` scans every source and XAML file. |
| Antivirus actions | `AntivirusCoordinator`: scheduling and starting scans are Safe Maintenance operations (refused in Observe Only), logged with how to undo; removing Guardian's own scheduled task is always allowed. Guardian never deletes or quarantines files itself: Defender's own remediation decides, and its quarantine is restorable from Windows Security > Protection history. Guardian never changes Defender's settings or exclusions. The scheduled task runs with `LeastPrivilege`, never as SYSTEM. |
| AI suspicion is never enough | There are no AI features in 0.1. Nothing quarantines files at all. |

## What Safe Maintenance can and cannot do

Can: move temp files you selected, that pass every safeguard, into the holding area; restore them; purge
held files after 7 days.

Cannot (not implemented, by design for 0.1): delete anything outside the holding area, empty caches of other
applications, clear browser data, change startup items, run package updates, or run antivirus scans.
