#!/usr/bin/env pwsh
# Build a vendorable, single-file Takatora CLI bundle.
#
#   ./publish.ps1                                  -> publish/Takatora
#   ./publish.ps1 -Dest D:\Game\Tools\Takatora     -> also copies the bundle there
#
# Output is a framework-dependent single file (needs .NET 8 runtime on the
# target machine): Takatora.Cli.exe + Takatora.Tasks.dll (loose, for the
# .fsx `#r`) + builtin-tasks/.
param(
    [string]$Output = "publish/Takatora",
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [string]$Dest
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $root $Output }

Write-Host "Publishing Takatora CLI ($Configuration / $Rid, single-file) ..." -ForegroundColor Cyan
if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }

dotnet publish (Join-Path $root "src/Takatora.Cli/Takatora.Cli.fsproj") `
    -c $Configuration -r $Rid --self-contained false -p:PublishSingleFile=true `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

Write-Host "`nBundle ready at $Output :" -ForegroundColor Green
Get-ChildItem $Output | Select-Object Name, @{N='Size';E={if ($_.PSIsContainer) {'<dir>'} else {'{0:N0} B' -f $_.Length}}} | Format-Table -AutoSize

if ($Dest) {
    if (-not [System.IO.Path]::IsPathRooted($Dest)) { $Dest = Join-Path (Get-Location) $Dest }
    Write-Host "Copying bundle to $Dest ..." -ForegroundColor Cyan
    if (-not (Test-Path $Dest)) { New-Item -ItemType Directory -Force $Dest | Out-Null }
    Copy-Item -Recurse -Force (Join-Path $Output '*') $Dest
    Write-Host "Done -> $Dest" -ForegroundColor Green
}
