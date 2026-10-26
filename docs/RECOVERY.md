# Recovery and troubleshooting

## Undo a cleanup
1. Open **Cleanup**. Files moved in the last 7 days are listed under **Holding area**.
2. Press **Restore**. The file goes back to its original path. Guardian will not overwrite a file that now exists there; rename or move that file first.
3. Manual restore (if the app won't start): held files are in `<data folder>/holding/<operation id>/`. The **Activity** log
   (or the `operations` table in `guardian.db`) shows each file's original path. Move it back yourself.

After 7 days held files are purged and the Activity log says the operation can no longer be undone.

## Undo a mode change
Mode tab → **Return to Observe Only**. This needs no confirmation and takes effect immediately.

## Repairs (Controlled Repair)
Windows DISM `/RestoreHealth` and SFC `/scannow` have **no undo**. Guardian tells you this before you approve and
logs them as irreversible. Before approving any repair:
- Back up your files (File History / OneDrive on Windows, Time Machine on macOS, your distribution's backup tool on Linux).
- Optionally create a System Restore point (Windows) or a Time Machine snapshot (macOS). These are extra safety nets,
  not substitutes for a backup: restore points don't cover personal files or every system component.

If Windows misbehaves after a repair: Settings → System → Recovery → "Fix problems using Windows Update", or System Restore.

## Guardian itself
| Problem | What to do |
|---|---|
| A check says "Permission required" | That check needs administrator/root. Run Guardian elevated once to see it, or ignore it. Guardian never escalates on its own. |
| A check says "Not available" | The tool or feature it needs isn't installed (for example ClamAV, ufw). This is not an error. |
| A check is "Inconclusive" | The tool's output wasn't in a format Guardian recognises. Expand **Evidence** to see the raw result. |
| "Skipped: failed twice in the last day" | Guardian stops retrying failing operations. Look at the error in **Activity**; it will allow another try after 24 hours. |
| Guardian won't start | Run `ai-laptop-guardian --report` in a terminal to see errors. Renaming the data folder resets Guardian to a fresh Observe Only state (your logs stay in the renamed folder). |
| Reset to factory state | Quit Guardian, delete (or rename) the data folder. Restore any held files first. |
