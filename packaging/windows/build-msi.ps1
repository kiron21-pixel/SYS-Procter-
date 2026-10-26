# Builds the MSI on Windows. Usage: pwsh packaging/windows/build-msi.ps1 [-Arch x64|arm64]
# Requires the .NET 10 SDK. Installs the WiX v5 .NET tool locally if it is missing.
# Signing: set $env:SIGN_CERT_THUMBPRINT to sign the exe and MSI with signtool (Authenticode).
param([ValidateSet('x64','arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'

# Native tools don't throw on failure, so check each exit code and surface the output (as a CI annotation too).
function Invoke-Checked([string]$What, [scriptblock]$Command) {
  $output = & $Command 2>&1 | Out-String
  Write-Host $output
  if ($LASTEXITCODE -ne 0) {
    $tail = ($output -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 15) -join ' | '
    Write-Host "::error title=$What failed::$tail"
    throw "$What failed with exit code $LASTEXITCODE"
  }
}

$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$version = (Select-Xml -Path "$root\Directory.Build.props" -XPath '//Version').Node.InnerText
$publish = "$root\artifacts\windows\publish-$Arch"
$out = "$root\artifacts\windows\AI-Laptop-Guardian-$version-$Arch.msi"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

Invoke-Checked 'dotnet publish' { dotnet publish "$root\src\Guardian.App\Guardian.App.csproj" -c Release -r "win-$Arch" --self-contained true -p:DebugType=none -o $publish }
if ($env:SIGN_CERT_THUMBPRINT) {
  Invoke-Checked 'signtool (exe)' { signtool sign /sha1 $env:SIGN_CERT_THUMBPRINT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "$publish\ai-laptop-guardian.exe" }
}

$toolDir = "$root\artifacts\tools"
$wix = "$toolDir\wix.exe"
if (-not (Test-Path $wix)) {
  Invoke-Checked 'install WiX' { dotnet tool install wix --version 5.0.2 --tool-path $toolDir }
}
Invoke-Checked 'wix build' { & $wix build "$PSScriptRoot\Package.wxs" -arch $Arch -d "Version=$version" -d "PublishDir=$publish" -o $out }
if (-not (Test-Path $out)) {
  Write-Host "::error title=MSI missing::wix build reported success but $out does not exist"
  throw "MSI not produced"
}
if ($env:SIGN_CERT_THUMBPRINT) {
  Invoke-Checked 'signtool (msi)' { signtool sign /sha1 $env:SIGN_CERT_THUMBPRINT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $out }
}
Write-Host "Built $out"
