# Publish Challenge Lab (self-contained win-x64) and copy SimConnect native DLL.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\ChallengeLab"
$sdkLib = "C:\MSFS 2024 SDK\SimConnect SDK\lib"

Write-Host "Publishing to $out ..."
dotnet publish (Join-Path $root "src\ChallengeLab.App\ChallengeLab.App.csproj") `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -o $out

$native = Join-Path $sdkLib "SimConnect.dll"
if (Test-Path $native) {
  Copy-Item $native $out -Force
  Write-Host "Copied SimConnect.dll"
} else {
  Write-Warning "Native SimConnect.dll not found at $native"
}

Write-Host ""
Write-Host "Done. Run: $out\ChallengeLab.exe"
Write-Host "Start MSFS 2024 first, then launch Challenge Lab."
