#requires -Version 5.1
<#
.SYNOPSIS
    Build a Zero Browser MSIX package + portable zip for Windows.

.DESCRIPTION
    Publishes the .NET 8 self-contained x64 build of ZeroBrowser.App,
    stages the manifest + tile assets, packs an MSIX with MakeAppx, then
    signs it with a self-signed test certificate (or with an externally
    supplied PFX if SIGNING_PFX_BASE64 + SIGNING_PFX_PWD env vars exist).

.PARAMETER Version
    Four-part numeric version stamped into AppxManifest (e.g. "0.1.0.0").
    Defaults to 0.1.0.0.

.PARAMETER OutputDir
    Where to drop the .msix, .cer (public certificate), and the portable
    .zip artifacts. Defaults to ./artifacts.

.NOTES
    Designed to run on the GitHub Actions windows-latest runner. Requires:
      - .NET 8 SDK
      - Windows 10 SDK (for MakeAppx.exe + signtool.exe)
      - PowerShell 5.1 or later
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0.0",
    [string]$OutputDir = "$PSScriptRoot/../artifacts"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot   = Resolve-Path "$PSScriptRoot/.."
$projPath   = Join-Path $repoRoot "src/ZeroBrowser.App/ZeroBrowser.App.csproj"
$manifest   = Join-Path $repoRoot "installer/windows/Package.appxmanifest"
$tilesDir   = Join-Path $repoRoot "installer/windows/Assets"
$publishDir = Join-Path $repoRoot "src/ZeroBrowser.App/bin/Release/net8.0/win-x64/publish"
$stageDir   = Join-Path $repoRoot "installer/windows/_stage"
$artifacts  = $OutputDir
New-Item -Force -ItemType Directory -Path $artifacts | Out-Null

Write-Host "==> Publishing self-contained Windows x64 build..." -ForegroundColor Cyan
& dotnet publish $projPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> Staging MSIX layout..." -ForegroundColor Cyan
if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
New-Item -ItemType Directory -Path $stageDir | Out-Null
Copy-Item -Recurse -Force "$publishDir/*" $stageDir
New-Item -ItemType Directory -Path "$stageDir/Assets" -Force | Out-Null
Copy-Item -Force "$tilesDir/*.png" "$stageDir/Assets/"

# Stamp version into the manifest
$manifestStaged = Join-Path $stageDir "AppxManifest.xml"
$manifestText = Get-Content $manifest -Raw
$manifestText = $manifestText -replace 'Version="0\.1\.0\.0"', "Version=""$Version"""
Set-Content -Path $manifestStaged -Value $manifestText -Encoding UTF8

# Locate Windows SDK tools
function Get-SdkTool($name) {
    $candidates = Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits\10\bin" `
                                -Recurse -Filter $name -ErrorAction SilentlyContinue |
                  Where-Object { $_.FullName -match "x64\\$name$" } |
                  Sort-Object FullName -Descending
    if (-not $candidates) { throw "$name not found in Windows SDK." }
    return $candidates[0].FullName
}
$makeAppx  = Get-SdkTool "MakeAppx.exe"
$signTool  = Get-SdkTool "signtool.exe"
Write-Host "  MakeAppx : $makeAppx"
Write-Host "  SignTool : $signTool"

Write-Host "==> Packaging MSIX..." -ForegroundColor Cyan
$msixPath = Join-Path $artifacts "ZeroBrowser-x64-$Version.msix"
& $makeAppx pack /d $stageDir /p $msixPath /overwrite /verbose
if ($LASTEXITCODE -ne 0) { throw "MakeAppx pack failed" }

Write-Host "==> Resolving signing certificate..." -ForegroundColor Cyan
$pfxPath = Join-Path $stageDir "_signing.pfx"
$cerPath = Join-Path $artifacts "ZeroBrowser-x64-$Version.cer"
if ($env:SIGNING_PFX_BASE64 -and $env:SIGNING_PFX_PWD) {
    Write-Host "  Using external PFX from env."
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:SIGNING_PFX_BASE64))
    $pfxPwd = ConvertTo-SecureString $env:SIGNING_PFX_PWD -AsPlainText -Force
} else {
    Write-Host "  No external PFX — generating self-signed cert (CN=Zero Browser)."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject "CN=Zero Browser" `
        -KeyUsage DigitalSignature `
        -FriendlyName "Zero Browser self-signed (release pipeline)" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    $pfxPwd = ConvertTo-SecureString -String "zerobrowser" -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pfxPwd | Out-Null
    Export-Certificate   -Cert $cert -FilePath $cerPath | Out-Null
}

Write-Host "==> Signing MSIX..." -ForegroundColor Cyan
$pwdPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($pfxPwd))
& $signTool sign /fd SHA256 /a /f $pfxPath /p $pwdPlain $msixPath
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }

Write-Host "==> Building portable zip..." -ForegroundColor Cyan
$zipPath = Join-Path $artifacts "ZeroBrowser-x64-$Version-portable.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath

Write-Host ""
Write-Host "==> Artifacts:" -ForegroundColor Green
Get-ChildItem $artifacts | Select-Object Name, Length | Format-Table | Out-String | Write-Host
