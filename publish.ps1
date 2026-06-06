#!/usr/bin/env pwsh
# Build vendorable, single-file Takatora bundles (CLI and/or GUI).
#
#   ./publish.ps1                                  -> publish/Takatora + publish/Takatora-Gui
#   ./publish.ps1 -Target Cli                      -> CLI bundle only
#   ./publish.ps1 -Target Gui                      -> GUI bundle only
#   ./publish.ps1 -Dest D:\Game\Tools\Takatora     -> also copy the CLI bundle there
#
# Output is framework-dependent single-file (needs .NET 8 runtime on the box):
#   CLI -> Takatora.Cli.exe + Takatora.Tasks.dll + builtin-tasks/
#   GUI -> Takatora.Gui.exe + Takatora.Tasks.dll + builtin-tasks/
# Takatora.Tasks.dll is kept loose (the .fsx task wrappers `#r` it by path);
# the GUI also self-extracts its Avalonia/Skia native libs from the exe.
param(
    [ValidateSet('Cli','Gui','Both')][string]$Target = 'Both',
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "publish",
    [string]$Dest
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot = Join-Path $root $OutputRoot }

function Publish-Bundle([string]$Proj, [string]$Out, [string[]]$Extra) {
    Write-Host "Publishing $Proj ($Configuration / $Rid, single-file) -> $Out ..." -ForegroundColor Cyan
    if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
    # Pipe to Out-Host so dotnet's build chatter doesn't leak into the return.
    dotnet publish (Join-Path $root $Proj) `
        -c $Configuration -r $Rid --self-contained false -p:PublishSingleFile=true @Extra `
        -o $Out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $Proj (exit $LASTEXITCODE)" }
    Get-ChildItem $Out |
        Select-Object Name, @{N='Size';E={if ($_.PSIsContainer) {'<dir>'} else {'{0:N0} B' -f $_.Length}}} |
        Format-Table -AutoSize | Out-Host
}

$cliOut = Join-Path $OutputRoot "Takatora"
$guiOut = Join-Path $OutputRoot "Takatora-Gui"

if ($Target -in 'Cli','Both') {
    Publish-Bundle "src/Takatora.Cli/Takatora.Cli.fsproj" $cliOut @()
}
if ($Target -in 'Gui','Both') {
    # Embed Avalonia/Skia native libs so the GUI is a single exe (+ Tasks.dll).
    Publish-Bundle "src/Takatora.Gui/Takatora.Gui.fsproj" $guiOut @('-p:IncludeNativeLibrariesForSelfExtract=true')
    Write-Host "Launch the GUI:  $guiOut\Takatora.Gui.exe" -ForegroundColor Green
}

if ($Dest) {
    if ($Target -eq 'Gui') { throw "-Dest copies the CLI bundle, but -Target was 'Gui'" }
    if (-not [System.IO.Path]::IsPathRooted($Dest)) { $Dest = Join-Path (Get-Location) $Dest }
    Write-Host "Copying CLI bundle to $Dest ..." -ForegroundColor Cyan
    if (-not (Test-Path $Dest)) { New-Item -ItemType Directory -Force $Dest | Out-Null }
    Copy-Item -Recurse -Force (Join-Path $cliOut '*') $Dest
    Write-Host "Done -> $Dest" -ForegroundColor Green
}
