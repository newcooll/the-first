param(
    [switch]$OpenOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptDir 'src/CDriveMaster.UI/CDriveMaster.UI.csproj'

if (-not (Test-Path $projectPath)) {
    throw "Project file not found: $projectPath"
}

[xml]$projXml = Get-Content -Path $projectPath -Raw
$versionNode = $projXml.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($versionNode)) {
    throw "<Version> is missing in src/CDriveMaster.UI/CDriveMaster.UI.csproj. Please add it before publishing."
}
$version = $versionNode.Trim()

$config = 'Release'
$runtime = 'win-x64'

$publishDir = Join-Path $scriptDir 'artifacts/publish/win-x64'
$releaseDir = Join-Path $scriptDir 'artifacts/release'

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}

New-Item -Path $publishDir -ItemType Directory -Force | Out-Null
New-Item -Path $releaseDir -ItemType Directory -Force | Out-Null

Write-Host "Publishing CDriveMaster.UI..."
Write-Host "Restoring runtime-specific assets..."
dotnet restore $projectPath -r $runtime --disable-parallel --no-cache
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE"
}

Write-Host "Publishing CDriveMaster.UI..."
dotnet publish $projectPath -c $config -r $runtime --no-restore /p:PublishSelfContained=true /p:PublishSingleFile=true /p:PublishTrimmed=false -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exePath = Join-Path $publishDir 'CDriveMaster.UI.exe'
if (-not (Test-Path $exePath)) {
    throw "Published EXE not found: $exePath"
}

Write-Host "Running smoke test (6 seconds)..."
$proc = Start-Process -FilePath $exePath -WorkingDirectory $publishDir -PassThru
Start-Sleep -Seconds 6

if ($proc.HasExited) {
    throw "Smoke test failed: app exited early with code $($proc.ExitCode)."
}

Stop-Process -Id $proc.Id -Force
Write-Host "Smoke test passed."

$timestamp = Get-Date -Format 'yyyyMMdd_HHmm'
$zipName = "CDriveMaster_v${version}_${timestamp}.zip"
$zipPath = Join-Path $releaseDir $zipName

Compress-Archive -Path $exePath -DestinationPath $zipPath -CompressionLevel Optimal -Force

Write-Host ""
Write-Host "Release package created successfully."
Write-Host "Version: $version"
Write-Host "EXE: $exePath"
Write-Host "ZIP: $zipPath"
Write-Host ""
Write-Host "Manual check (quick):"
Write-Host "1) Launch EXE and verify UI opens."
Write-Host "2) Run scan and verify progress/cancel."
Write-Host "3) Verify Recycle Bin and Temp cleanup flows."

if ($OpenOutput) {
    Start-Process explorer.exe $releaseDir
}
