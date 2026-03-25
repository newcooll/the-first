param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Version = "0.6.0-beta"
)

$ErrorActionPreference = "Stop"

# Resolve project root from script location: scripts/publish-beta.ps1 -> project root
$projectRoot = Split-Path -Parent $PSScriptRoot
$uiProject = Join-Path $projectRoot "src/CDriveMaster.UI/CDriveMaster.UI.csproj"
$coreRules = Join-Path $projectRoot "src/CDriveMaster.Core/Rules"
$docsPath = Join-Path $projectRoot "docs"
$publishTemp = Join-Path $projectRoot "artifacts/publish/$RuntimeIdentifier"
$finalOutput = Join-Path $projectRoot "publish_output"

Write-Host "[1/5] Cleaning output folders..."
if (Test-Path $finalOutput) {
    Remove-Item -Path $finalOutput -Recurse -Force
}
if (Test-Path $publishTemp) {
    Remove-Item -Path $publishTemp -Recurse -Force
}
New-Item -ItemType Directory -Path $finalOutput | Out-Null
New-Item -ItemType Directory -Path $publishTemp -Force | Out-Null

Write-Host "[2/5] Running dotnet publish for $RuntimeIdentifier..."
# Publish settings:
# - Release build
# - Single-file executable
# - Self-contained deployment for machines without .NET runtime
# - Trimming disabled to avoid WPF reflection/dynamic binding breakage
# - WinExe output comes from the WPF UI project configuration
# - InformationalVersion is injected at publish time for footer/version display
$publishArgs = @(
    "publish", $uiProject,
    "-c", "Release",
    "-r", $RuntimeIdentifier,
    "-o", $publishTemp,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:PublishTrimmed=false",
    "-p:InformationalVersion=$Version"
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "[3/5] Locating generated executable..."
if (-not (Test-Path $publishTemp)) {
    throw "Publish output folder not found: $publishTemp"
}

$exe = Get-ChildItem -Path $publishTemp -Filter "*.exe" -File |
    Where-Object { $_.Name -notlike "*vshost*" } |
    Select-Object -First 1

if (-not $exe) {
    throw "No executable file found under: $publishTemp"
}

Write-Host "[4/5] Copying executable to publish_output..."
Copy-Item -Path $exe.FullName -Destination (Join-Path $finalOutput $exe.Name) -Force

Write-Host "[5/5] Copying rules and docs to publish_output..."
if (-not (Test-Path $coreRules)) {
    throw "Rules folder not found: $coreRules"
}
Copy-Item -Path $coreRules -Destination (Join-Path $finalOutput "Rules") -Recurse -Force

if (Test-Path $docsPath) {
    Copy-Item -Path $docsPath -Destination (Join-Path $finalOutput "docs") -Recurse -Force
}

Write-Host "Publish completed successfully."
Write-Host "Output: $finalOutput"
