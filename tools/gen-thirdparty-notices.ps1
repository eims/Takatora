#!/usr/bin/env pwsh
# Generate THIRD-PARTY-NOTICES.txt from the resolved NuGet dependencies of the
# shipped binaries (CLI + GUI). Reads each project's obj/project.assets.json for
# the resolved package set, then pulls the license expression (from the .nuspec)
# and the bundled license text (LICENSE / COPYING / the nuspec's <license
# type="file">) from the local NuGet cache. No hand-typed license texts.
#
# Run after changing dependencies:  pwsh ./tools/gen-thirdparty-notices.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$assets = @(
    "src/Takatora.Cli/obj/project.assets.json"
    "src/Takatora.Gui/obj/project.assets.json"
) | ForEach-Object { Join-Path $root $_ }

# Union of resolved packages ("Id/Version") across the shipped projects.
$pkgs = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($a in $assets) {
    if (-not (Test-Path $a)) { throw "missing $a — build the solution first (dotnet build)" }
    $j = Get-Content $a -Raw | ConvertFrom-Json
    foreach ($lib in $j.libraries.PSObject.Properties) {
        if ($lib.Value.type -eq 'package') { [void]$pkgs.Add($lib.Name) }
    }
}

$cache = Join-Path $env:USERPROFILE ".nuget/packages"

# Legacy licenseUrl values → SPDX id (older packages predate <license expression>).
$urlToSpdx = @{
    'https://go.microsoft.com/fwlink/?linkid=868514'             = 'MIT'  # SkiaSharp / HarfBuzzSharp
    'https://github.com/dotnet/corefx/blob/master/LICENSE.TXT'   = 'MIT'  # .NET Foundation libs
}
# Packages whose nuspec declares no license but whose project license is known.
$idToSpdx = @{ 'Avalonia.BuildServices' = 'MIT' }

function Get-Pkg([string]$idver) {
    $id, $ver = $idver -split '/', 2
    $dir = Join-Path $cache (Join-Path $id.ToLower() $ver.ToLower())
    $nuspec = Join-Path $dir ($id.ToLower() + ".nuspec")
    $license = "(unspecified)"
    $fileRel = $null
    if (Test-Path $nuspec) {
        try {
            $x = [xml](Get-Content $nuspec -Raw)
            $lic = $x.package.metadata.license
            if ($lic) {
                if ($lic.type -eq 'expression') { $license = $lic.'#text' }
                elseif ($lic.type -eq 'file') { $license = "see bundled file"; $fileRel = $lic.'#text' }
            } elseif ($x.package.metadata.licenseUrl) { $license = $x.package.metadata.licenseUrl }
        } catch {}
    }
    # bundled license text: the nuspec-named file, else a conventional name.
    $text = $null
    $candidate = $null
    if ($fileRel) { $candidate = Join-Path $dir $fileRel }
    if (-not ($candidate -and (Test-Path $candidate))) {
        $candidate = Get-ChildItem $dir -File -Recurse -Depth 1 -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING)(\.(txt|md))?$' } |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ($candidate -and (Test-Path $candidate)) { $text = (Get-Content $candidate -Raw).TrimEnd() }
    if ($urlToSpdx.ContainsKey($license)) { $license = $urlToSpdx[$license] }
    if ($license -eq '(unspecified)' -and $idToSpdx.ContainsKey($id)) { $license = $idToSpdx[$id] }
    [pscustomobject]@{ Id = $id; Version = $ver; License = $license; Text = $text }
}

$sb = [System.Text.StringBuilder]::new()
$nl = { param($s) [void]$sb.AppendLine($s) }
& $nl "Takatora - THIRD-PARTY NOTICES"
& $nl "=============================="
& $nl ""
& $nl "The Takatora binaries (takatora CLI / GUI) bundle the third-party"
& $nl "components listed below, each provided under its own license. The .NET"
& $nl "runtime is supplied separately by Microsoft (MIT) and is not redistributed."
& $nl "Generated from the resolved NuGet dependencies by tools/gen-thirdparty-notices.ps1."
& $nl ""
& $nl ("Components (" + $pkgs.Count + "):")
foreach ($idver in $pkgs) {
    $p = Get-Pkg $idver
    & $nl ("  - {0} {1} - {2}" -f $p.Id, $p.Version, $p.License)
}
& $nl ""
& $nl "=================================================================="
& $nl "Full license texts (as bundled by each package)"
& $nl "=================================================================="
foreach ($idver in $pkgs) {
    $p = Get-Pkg $idver
    if ($p.Text) {
        & $nl ""
        & $nl ("------------------------------------------------------------------")
        & $nl ("{0} {1} - {2}" -f $p.Id, $p.Version, $p.License)
        & $nl ("------------------------------------------------------------------")
        & $nl $p.Text
    }
}

# For SPDX ids that no bundled package provided the text for (e.g. Apache-2.0,
# BSD-2-Clause), fetch the canonical text from SPDX (best-effort; needs network).
$haveText = @{}
$needSpdx = [System.Collections.Generic.SortedSet[string]]::new()
foreach ($idver in $pkgs) {
    $p = Get-Pkg $idver
    if ($p.License -match '^[A-Za-z0-9][A-Za-z0-9.+-]*$') {
        if ($p.Text) { $haveText[$p.License] = $true } else { [void]$needSpdx.Add($p.License) }
    }
}
foreach ($spdx in $needSpdx) {
    if ($haveText.ContainsKey($spdx)) { continue }
    $url = "https://raw.githubusercontent.com/spdx/license-list-data/main/text/$spdx.txt"
    try {
        $txt = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 20).Content.TrimEnd()
        & $nl ""
        & $nl "------------------------------------------------------------------"
        & $nl "$spdx (canonical SPDX license text; not bundled by the package)"
        & $nl "------------------------------------------------------------------"
        & $nl $txt
        $haveText[$spdx] = $true
    } catch {
        Write-Host ("  WARN: {0} has no bundled text and the SPDX fetch failed (rerun with network)" -f $spdx)
    }
}

$out = Join-Path $root "THIRD-PARTY-NOTICES.txt"
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))
Write-Host ("Wrote {0} ({1} components, {2} lines)" -f $out, $pkgs.Count, ($sb.ToString() -split "`n").Count)
$withText = ($pkgs | ForEach-Object { (Get-Pkg $_).Text } | Where-Object { $_ } | Measure-Object).Count
Write-Host ("  bundled license texts found: {0}/{1}" -f $withText, $pkgs.Count)
