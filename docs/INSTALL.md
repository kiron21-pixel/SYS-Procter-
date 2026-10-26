# Install and uninstall

All installers are **0.1 preview** builds. Only the Linux .deb has been built and tested so far (see PLATFORM_STATUS.md).
None of them install services, drivers, scheduled tasks, firewall rules, browser extensions or startup items, and none
change other software.

## Windows (MSI)
Build on Windows: `pwsh packaging/windows/build-msi.ps1 -Arch x64` (needs .NET 10 SDK; installs WiX v5 if missing).
Set `SIGN_CERT_THUMBPRINT` to Authenticode-sign the exe and MSI; unsigned builds trigger SmartScreen warnings.

- Install: double-click the MSI. It installs to `C:\Program Files\AI Laptop Guardian` and adds a Start menu shortcut.
- Uninstall: Settings → Apps → Installed apps → AI Laptop Guardian → Uninstall (or `msiexec /x <file>.msi`).
- Then, if you want, delete `%LOCALAPPDATA%\AI Laptop Guardian` (logs and held files).

## macOS (.pkg)
Build on a Mac: `packaging/macos/build-pkg.sh arm64` (or `x64`). For a build users can open, provide
`DEVELOPER_ID_APP`, `DEVELOPER_ID_INSTALLER` and `NOTARY_PROFILE` (Apple Developer Program membership required).

- Install: open the .pkg. It installs `/Applications/AI Laptop Guardian.app`.
- Uninstall: drag the app to the Bin, then `sudo pkgutil --forget app.ailaptopguardian.pkg`.
- Then, if you want, delete `~/Library/Application Support/AI Laptop Guardian`.

## Ubuntu / Debian (.deb)
Build: `packaging/linux/build-deb.sh x64` → `artifacts/linux/ai-laptop-guardian_<version>_amd64.deb`.

- Install: `sudo apt install ./ai-laptop-guardian_0.1.0_amd64.deb`
- Uninstall: `sudo apt remove ai-laptop-guardian`
- Then, if you want, delete `~/.local/share/ai-laptop-guardian`.

The package has no install/remove scripts, so installing and removing run no code.

## Fedora (.rpm)
Publish, then `rpmbuild -bb packaging/linux/ai-laptop-guardian.spec --define "_publishdir $PWD/publish" --define "_repo $PWD"`.
**Not yet built or tested.**

- Install: `sudo dnf install ./ai-laptop-guardian-0.1.0-1.x86_64.rpm`
- Uninstall: `sudo dnf remove ai-laptop-guardian`

## From source (any OS)
```bash
dotnet run --project src/Guardian.App
```
