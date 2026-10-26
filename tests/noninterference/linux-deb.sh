#!/usr/bin/env bash
# Non-interference test for the .deb on a DISPOSABLE Ubuntu/Debian machine (VM or container).
# Snapshots system state, installs Guardian, runs the read-only report, uninstalls, and diffs.
# Never run this on your main computer: it installs and removes packages.
set -euo pipefail
[[ "${GUARDIAN_DISPOSABLE_VM:-}" == "1" ]] || { echo "Refusing: set GUARDIAN_DISPOSABLE_VM=1 on a throwaway VM/container."; exit 2; }
DEB="$1"
WORK="$(mktemp -d)"
snapshot() {
  local d="$WORK/$1"; mkdir -p "$d"
  dpkg-query -W -f='${Package} ${Version} ${Status}\n' | sort > "$d/packages"
  find /etc -xdev -type f -readable -print0 2>/dev/null | sort -z | xargs -0 sha256sum 2>/dev/null > "$d/etc"
  (systemctl list-unit-files --no-legend --no-pager 2>/dev/null || true) | sort > "$d/units"
  (crontab -l 2>/dev/null || true) > "$d/crontab"
  find "$HOME" -xdev -path "$HOME/.local/share/ai-laptop-guardian" -prune -o -path "$HOME/.cache" -prune -o -path "$HOME/.dotnet" -prune -o -path "$HOME/.nuget" -prune -o -type f -print0 2>/dev/null \
    | sort -z | xargs -0 sha256sum 2>/dev/null > "$d/home" || true
  find /usr /opt -xdev \( -type f -o -type l \) 2>/dev/null | sort > "$d/usr-opt"
}
snapshot before
apt-get install -y -qq "$DEB" > "$WORK/install.log"
NEW_DEPS="$(comm -13 <(cut -d' ' -f1 "$WORK/before/packages") <(dpkg-query -W -f='${Package}\n' | sort) | grep -v '^ai-laptop-guardian$' || true)"
/usr/bin/ai-laptop-guardian --report > "$WORK/report.txt"
grep -q "This is not a guarantee" "$WORK/report.txt"
apt-get remove -y -qq ai-laptop-guardian > "$WORK/remove.log"
[[ -n "$NEW_DEPS" ]] && apt-get remove -y -qq $NEW_DEPS > /dev/null && apt-get autoremove -y -qq > /dev/null
snapshot after

fail=0
for f in packages etc units crontab home usr-opt; do
  if ! diff -q "$WORK/before/$f" "$WORK/after/$f" > /dev/null; then
    echo "CHANGED: $f"; diff "$WORK/before/$f" "$WORK/after/$f" | head -20; fail=1
  else
    echo "unchanged: $f"
  fi
done
echo "Dependencies pulled in by the package (and removed again): ${NEW_DEPS:-none}"
echo "Guardian's own data (kept on uninstall, by design): $HOME/.local/share/ai-laptop-guardian"
[[ $fail == 0 ]] && echo "PASS: no unintended changes outside Guardian's own data folder." || { echo "FAIL"; exit 1; }
