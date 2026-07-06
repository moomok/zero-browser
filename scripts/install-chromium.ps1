param(
    [string]$Version = "138.0.7204.157",
    [string]$Arch = "win64",
    [string]$VendorDir = "$PSScriptRoot\..\vendor\chromium"
)

$ErrorActionPreference = "Stop"

$zipName = "chrome-win.zip"
$url = "https://storage.googleapis.com/chromium-browser-snapshots/$Arch/$Version/chrome-win.zip"

New-Item -ItemType Directory -Path $VendorDir -Force | Out-Null
$zipPath = Join-Path $VendorDir $zipName

Write-Host "Downloading Chromium $Version ($Arch)..."
Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing

Write-Host "Extracting..."
Expand-Archive -Path $zipPath -DestinationPath $VendorDir -Force

Write-Host "Cleaning up zip..."
Remove-Item -LiteralPath $zipPath -Force

$exe = Join-Path $VendorDir "chrome-win\chrome.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "chrome.exe not found at $exe after extraction."
}

Write-Host "Done. Chromium installed at: $exe"
