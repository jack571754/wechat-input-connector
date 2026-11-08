# Distribution packager for WeChatInputConnector
param(
    [switch]$SkipTests = $false,
    [switch]$SkipSlim = $false
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " WeChatInputConnector Packaging & Optimization " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Run unit tests
if (-not $SkipTests) {
    Write-Host "[1/4] Running unit tests..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "dev.ps1") test
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed, aborting packaging." }
    Write-Host " Unit tests passed successfully." -ForegroundColor Green
} else {
    Write-Host "[1/4] Skipping unit tests." -ForegroundColor DarkGray
}

$artifactsDir = Join-Path $projectRoot "artifacts"
$hardenedDir = Join-Path $artifactsDir "desktop-hardened"
$distDir = Join-Path $artifactsDir "dist"
New-Item -ItemType Directory -Path $hardenedDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

# 2. Publish Self-Contained Trimmed Edition
Write-Host "[2/4] Publishing Self-Contained Trimmed Edition..." -ForegroundColor Yellow
& (Join-Path $PSScriptRoot "dev.ps1") publish "$projectRoot/tools/Connector.Probe/Connector.Probe.csproj" `
    -c Release -r win-x64 --self-contained true `
    -o $hardenedDir
if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed." }

# Sync to p0-current for local use
$currentDir = Join-Path $artifactsDir "p0-current"
New-Item -ItemType Directory -Path $currentDir -Force | Out-Null
Copy-Item -Path "$hardenedDir/*" -Destination $currentDir -Recurse -Force

# Create self-contained zip
$selfContainedZip = Join-Path $distDir "WeChatInputConnector-v0.2.0-win-x64.zip"
if (Test-Path -LiteralPath $selfContainedZip) { Remove-Item -LiteralPath $selfContainedZip -Force }
Write-Host "  Compressing self-contained package to $selfContainedZip..." -ForegroundColor DarkGray
Compress-Archive -Path "$hardenedDir/*" -DestinationPath $selfContainedZip -Force

$hardenedFiles = (Get-ChildItem -LiteralPath $hardenedDir).Count
$hardenedSizeMB = [math]::Round(((Get-ChildItem -LiteralPath $hardenedDir | Measure-Object -Property Length -Sum).Sum / 1MB), 2)
$hardenedZipSizeMB = [math]::Round(((Get-Item -LiteralPath $selfContainedZip).Length / 1MB), 2)

Write-Host " Self-contained package complete: $hardenedFiles files, uncompressed ${hardenedSizeMB} MB, zip ${hardenedZipSizeMB} MB." -ForegroundColor Green

# 3. Publish Framework-Dependent Slim Edition
if (-not $SkipSlim) {
    Write-Host "[3/4] Publishing Framework-Dependent Slim Edition..." -ForegroundColor Yellow
    $slimDir = Join-Path $artifactsDir "desktop-slim"
    New-Item -ItemType Directory -Path $slimDir -Force | Out-Null

    & (Join-Path $PSScriptRoot "dev.ps1") publish "$projectRoot/tools/Connector.Probe/Connector.Probe.csproj" `
        -c Release -r win-x64 --self-contained false `
        -p:PublishTrimmed=false -p:DebugType=none `
        -o $slimDir
    if ($LASTEXITCODE -ne 0) { throw "Slim publish failed." }

    $slimZip = Join-Path $distDir "WeChatInputConnector-v0.2.0-slim.zip"
    if (Test-Path -LiteralPath $slimZip) { Remove-Item -LiteralPath $slimZip -Force }
    Write-Host "  Compressing slim package to $slimZip..." -ForegroundColor DarkGray
    Compress-Archive -Path "$slimDir/*" -DestinationPath $slimZip -Force

    $slimFiles = (Get-ChildItem -LiteralPath $slimDir).Count
    $slimSizeMB = [math]::Round(((Get-ChildItem -LiteralPath $slimDir | Measure-Object -Property Length -Sum).Sum / 1MB), 2)
    $slimZipSizeMB = [math]::Round(((Get-Item -LiteralPath $slimZip).Length / 1MB), 2)

    Write-Host " Slim package complete: $slimFiles files, uncompressed ${slimSizeMB} MB, zip ${slimZipSizeMB} MB." -ForegroundColor Green
    Remove-Item -Path $slimDir -Recurse -Force -ErrorAction SilentlyContinue
}

# 4. Generate Checksums
Write-Host "[4/4] Generating SHA256 checksums..." -ForegroundColor Yellow
$checksums = Get-ChildItem -LiteralPath $distDir -Filter *.zip | Get-FileHash -Algorithm SHA256 |
    Select-Object @{Name='File';Expression={Split-Path $_.Path -Leaf}},
                  @{Name='SizeMB';Expression={[math]::Round((Get-Item $_.Path).Length / 1MB, 2)}},
                  Hash
$checksums | Format-Table -AutoSize
$checksums | ConvertTo-Json -Depth 2 | Set-Content -LiteralPath (Join-Path $distDir "checksums.json") -Encoding utf8

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Packaging successfully completed! Output in artifacts/dist/" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
