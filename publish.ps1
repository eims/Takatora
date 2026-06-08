#!/usr/bin/env pwsh
# Build vendorable, single-file Takatora bundles (CLI and/or GUI).
#
#   ./publish.ps1                                  -> publish/Takatora (CLI + GUI together)
#   ./publish.ps1 -Target Cli                      -> CLI bundle only  (publish/Takatora)
#   ./publish.ps1 -Target Gui                      -> GUI bundle only  (publish/Takatora-Gui)
#   ./publish.ps1 -Dest D:\Game\Tools\Takatora     -> also copy the bundle there
#
# Output is framework-dependent single-file (needs .NET 8 runtime on the box).
# Both exes resolve Takatora.Tasks.dll + builtin-tasks/ from their own folder,
# so when shipping both they share ONE copy in a single folder:
#   takatora.exe + Takatora.Gui.exe + Takatora.Tasks.dll + builtin-tasks/
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

# Any exe still running from under the output dir would lock its files and
# break the clean below. Stop ONLY processes launched from under $OutputRoot
# (by path, so it catches takatora.exe / Takatora.Gui.exe / older names alike)
# — dev builds (bin/) and vendored copies elsewhere are left alone.
Get-Process -ErrorAction SilentlyContinue |
    Where-Object {
        try { $_.Path -and $_.Path.StartsWith($OutputRoot, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    } |
    ForEach-Object {
        Write-Host "Stopping $($_.ProcessName) running from the publish dir (pid $($_.Id))" -ForegroundColor Yellow
        try { $_ | Stop-Process -Force } catch {}
    }
Start-Sleep -Milliseconds 400  # let Windows release the file handles

# Empty a directory by deleting its CONTENTS (not the folder itself), so an
# Explorer window open on the folder — which locks the directory handle — does
# not break the clean. The folder is created if absent.
function Clear-OutDir([string]$Out) {
    if (Test-Path $Out) {
        Get-ChildItem -LiteralPath $Out -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Force $Out | Out-Null
    }
}

function Publish-Bundle([string]$Proj, [string]$Out, [string[]]$Extra) {
    Write-Host "Publishing $Proj ($Configuration / $Rid, single-file) -> $Out ..." -ForegroundColor Cyan
    Clear-OutDir $Out
    # Pipe to Out-Host so dotnet's build chatter doesn't leak into the return.
    # NB: use -p:SelfContained=false, NOT --self-contained false. The CLI flag
    # form isn't honored alongside -p:PublishSingleFile=true (the bundle ends
    # up including the whole .NET runtime, ~95MB); the -p: form is respected
    # and yields a proper framework-dependent single file (~27MB).
    dotnet publish (Join-Path $root $Proj) `
        -c $Configuration -r $Rid -p:SelfContained=false -p:PublishSingleFile=true @Extra `
        -o $Out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $Proj (exit $LASTEXITCODE)" }
}

function Show-Dir([string]$Out) {
    Get-ChildItem $Out |
        Select-Object Name, @{N='Size';E={if ($_.PSIsContainer) {'<dir>'} else {'{0:N0} B' -f $_.Length}}} |
        Format-Table -AutoSize | Out-Host
}

$cliProj = "src/Takatora.Cli/Takatora.Cli.fsproj"
$guiProj = "src/Takatora.Gui/Takatora.Gui.fsproj"
$guiExtra = @('-p:IncludeNativeLibrariesForSelfExtract=true')  # fold native libs into the GUI exe

# The folder we hand to -Dest (and announce). Set per target below.
$mainOut = $null

switch ($Target) {
    'Cli' {
        $mainOut = Join-Path $OutputRoot "Takatora"
        Publish-Bundle $cliProj $mainOut @()
        Show-Dir $mainOut
    }
    'Gui' {
        $mainOut = Join-Path $OutputRoot "Takatora-Gui"
        Publish-Bundle $guiProj $mainOut $guiExtra
        Show-Dir $mainOut
        Write-Host "Launch the GUI:  $mainOut\Takatora.Gui.exe" -ForegroundColor Green
    }
    'Both' {
        # One shared folder. Publish the CLI (gives Takatora.Tasks.dll +
        # builtin-tasks/), then publish the GUI to a temp and drop just its exe
        # alongside — both share the CLI's loose Tasks.dll + builtin-tasks/.
        $mainOut = Join-Path $OutputRoot "Takatora"
        Publish-Bundle $cliProj $mainOut @()
        $guiTmp = Join-Path $OutputRoot ".gui-tmp"
        Publish-Bundle $guiProj $guiTmp $guiExtra
        # Explicit file destination (NOT the dir): copying a file onto a
        # non-existent dir path would otherwise create a FILE named "Takatora".
        Copy-Item -Force (Join-Path $guiTmp "Takatora.Gui.exe") (Join-Path $mainOut "Takatora.Gui.exe")
        Remove-Item -Recurse -Force $guiTmp
        Show-Dir $mainOut
        Write-Host "Launch the GUI:  $mainOut\Takatora.Gui.exe" -ForegroundColor Green
        Write-Host "Run a flow:      $mainOut\takatora.exe run <project> <flow>" -ForegroundColor Green
    }
}

if ($Dest) {
    if (-not [System.IO.Path]::IsPathRooted($Dest)) { $Dest = Join-Path (Get-Location) $Dest }
    Write-Host "Copying bundle to $Dest ..." -ForegroundColor Cyan
    if (-not (Test-Path $Dest)) { New-Item -ItemType Directory -Force $Dest | Out-Null }
    Copy-Item -Recurse -Force (Join-Path $mainOut '*') $Dest
    Write-Host "Done -> $Dest" -ForegroundColor Green
}
