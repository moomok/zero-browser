param(
    [string]$GoBin,
    [string]$OutDir = "$PSScriptRoot\..\vendor\tls-sidecar"
)

if (-not $GoBin) {
    $detected = (Get-Command go -ErrorAction SilentlyContinue).Source
    if ($detected) {
        $GoBin = $detected
    } elseif ($IsWindows -or $env:OS -match 'Windows') {
        $GoBin = "C:\bot\go\bin\go.exe"
    } else {
        throw "Could not locate `go` in PATH. Set -GoBin or install Go (apt/brew)."
    }
}

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$targets = @(
    @{ GOOS = "windows"; GOARCH = "amd64"; Name = "zero-browser-tls-sidecar.exe" },
    @{ GOOS = "darwin";  GOARCH = "amd64"; Name = "zero-browser-tls-sidecar"     },
    @{ GOOS = "darwin";  GOARCH = "arm64"; Name = "zero-browser-tls-sidecar"     },
    @{ GOOS = "linux";   GOARCH = "amd64"; Name = "zero-browser-tls-sidecar"     },
    @{ GOOS = "linux";   GOARCH = "arm64"; Name = "zero-browser-tls-sidecar"     }
)

$env:GOSUMDB = "off"

Push-Location $PSScriptRoot
try {
    foreach ($t in $targets) {
        $env:GOOS = $t.GOOS
        $env:GOARCH = $t.GOARCH
        $targetDir = Join-Path $OutDir "$($t.GOOS)-$($t.GOARCH)"
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        $outFile = Join-Path $targetDir $t.Name
        Write-Host "Building $($t.GOOS)/$($t.GOARCH)..."
        & $GoBin build -ldflags="-s -w" -o $outFile .
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed for $($t.GOOS)/$($t.GOARCH)"
        }
    }
}
finally {
    Pop-Location
    Remove-Item Env:\GOOS -ErrorAction SilentlyContinue
    Remove-Item Env:\GOARCH -ErrorAction SilentlyContinue
}

Write-Host "Done. Output in $OutDir"
Get-ChildItem -Path $OutDir -Recurse -File | Select-Object FullName, @{Name="SizeMB";Expression={[math]::Round($_.Length / 1MB, 2)}}
