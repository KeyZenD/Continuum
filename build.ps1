param(
    [switch]$SkipTests,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\Continuum\Continuum.csproj'
$tests = Join-Path $root 'tests\Continuum.Tests\Continuum.Tests.csproj'
$dist = Join-Path $root 'dist'
$built = Join-Path $root 'src\Continuum\bin\Release\net10.0\Continuum.dll'

Write-Host 'Building Continuum...' -ForegroundColor Cyan
dotnet build $project -c Release -v:q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    dotnet run --project $tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item $built (Join-Path $dist 'Continuum.dll') -Force
Copy-Item (Join-Path $root 'metadata.json') (Join-Path $dist 'metadata.json') -Force

Compress-Archive -Path (Join-Path $dist 'Continuum.dll'), (Join-Path $dist 'metadata.json') `
    -DestinationPath (Join-Path $root 'Continuum.zip') -Force

Write-Host "Plugin ready in $dist" -ForegroundColor Green
Write-Host 'Release archive: Continuum.zip' -ForegroundColor Green

if ($Install) {
    $pluginDir = Join-Path $env:LOCALAPPDATA 'OpenTabletDriver\Plugins\Continuum'
    New-Item -ItemType Directory -Force $pluginDir | Out-Null
    Copy-Item (Join-Path $dist 'Continuum.dll') $pluginDir -Force
    Copy-Item (Join-Path $dist 'metadata.json') $pluginDir -Force
    Write-Host "Installed to $pluginDir (restart OpenTabletDriver)" -ForegroundColor Yellow
}
