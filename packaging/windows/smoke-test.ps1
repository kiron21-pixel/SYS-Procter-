# Installs the built MSI the way a user would, then checks the installed app actually starts.
# Usage (CI, on a disposable runner): pwsh packaging/windows/smoke-test.ps1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$msi = Get-ChildItem "$root\artifacts\windows\*.msi" | Select-Object -First 1
$exe = "$env:ProgramFiles\AI Laptop Guardian\ai-laptop-guardian.exe"
$log = "$env:LOCALAPPDATA\AI Laptop Guardian\crash.log"

$p = Start-Process msiexec.exe -ArgumentList '/i', "`"$($msi.FullName)`"", '/qn', '/l*v', "$root\artifacts\windows\install.log" -Wait -PassThru
if ($p.ExitCode -ne 0) { Get-Content "$root\artifacts\windows\install.log" -Tail 40; throw "msiexec failed with $($p.ExitCode)" }
if (-not (Test-Path $exe)) { throw "Installed exe not found at $exe" }
if (-not (Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\AI Laptop Guardian.lnk")) { throw "Start menu shortcut missing" }
if (-not (Test-Path "$env:PUBLIC\Desktop\AI Laptop Guardian.lnk")) { throw "Desktop shortcut missing" }

# 1. Headless report from the installed copy.
$out = & $exe --report | Out-String
Write-Host $out
if ($LASTEXITCODE -ne 0) { throw "--report exited with $LASTEXITCODE" }
if ($out -notmatch 'AI Laptop Guardian') { throw "--report printed nothing" }

# 2. Start the window and make sure it is still running after 20 seconds.
$app = Start-Process $exe -PassThru
Start-Sleep -Seconds 20
if ($app.HasExited) {
  if (Test-Path $log) { Get-Content $log }
  Write-Host "::error title=App exited on start::exit code $($app.ExitCode)"
  throw "The installed app closed by itself (exit code $($app.ExitCode))"
}
Write-Host "Window title: $($app.MainWindowTitle)"
Stop-Process -Id $app.Id
if (Test-Path $log) { Get-Content $log; throw "crash.log was written during the smoke test" }

$p = Start-Process msiexec.exe -ArgumentList '/x', "`"$($msi.FullName)`"", '/qn' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Uninstall failed with $($p.ExitCode)" }
if (Test-Path $exe) { throw "Uninstall left the exe behind" }
Write-Host "Install, report, window start and uninstall all passed."
