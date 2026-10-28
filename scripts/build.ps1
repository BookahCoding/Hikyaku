# Reproducible build: restore, build (warnings as errors), test, publish to ./artifacts. No installs, no system changes.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet build Hikyaku.sln -c $Configuration -nologo; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet test tests/Hikyaku.Tests -c $Configuration --no-build -nologo; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet test tests/Hikyaku.App.Tests -c $Configuration --no-build -nologo; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet publish src/Hikyaku.App -c $Configuration -r win-x64 --self-contained false -o artifacts/Hikyaku -nologo; if ($LASTEXITCODE) { exit $LASTEXITCODE }
Write-Host "Published: artifacts/Hikyaku/Hikyaku.exe"
