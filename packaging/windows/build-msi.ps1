# Builds the MSI on Windows. Usage: pwsh packaging/windows/build-msi.ps1 [-Arch x64|arm64]
# Requires the .NET 10 SDK. Installs the WiX v5 .NET tool locally if it is missing.
# Signing: set $env:SIGN_CERT_THUMBPRINT to sign the exe and MSI with signtool (Authenticode).
param([ValidateSet('x64','arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\..\.."
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version
$publish = "$root\artifacts\windows\publish-$Arch"
$out = "$root\artifacts\windows\AI-Laptop-Guardian-$version-$Arch.msi"

dotnet publish "$root\src\Guardian.App\Guardian.App.csproj" -c Release -r "win-$Arch" --self-contained true -p:DebugType=none -o $publish
if ($env:SIGN_CERT_THUMBPRINT) {
  signtool sign /sha1 $env:SIGN_CERT_THUMBPRINT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "$publish\ai-laptop-guardian.exe"
}
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { dotnet tool install --global wix --version 5.0.2 }
wix build "$PSScriptRoot\Package.wxs" -arch $Arch -d Version=$version -d PublishDir=$publish -o $out
if ($env:SIGN_CERT_THUMBPRINT) {
  signtool sign /sha1 $env:SIGN_CERT_THUMBPRINT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $out
}
Write-Host "Built $out"
