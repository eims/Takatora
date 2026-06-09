#!/usr/bin/env pwsh
# Generate docs/builtin-tasks.md from the header comment of each bundled task
# (.fsx). The headers already document the task + its params/outputs; this just
# collects them into one reference. Rerun after adding/editing a builtin task.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$tasksDir = Join-Path $root "src/Takatora.Tasks.Builtin/tasks"
$files = Get-ChildItem $tasksDir -Filter *.fsx | Sort-Object Name

$sb = [System.Text.StringBuilder]::new()
$nl = { param($s) [void]$sb.AppendLine($s) }
& $nl "# Built-in tasks"
& $nl ""
& $nl 'Reference for the task types Takatora ships with. A flow step''s `type = "<task>"`'
& $nl 'runs one of these — or a project-local `.takatora/tasks/<task>.fsx` of the same'
& $nl 'name, which takes precedence (the custom-task escape hatch). For the'
& $nl 'machine-readable param/output schema, run `takatora describe <task> [--project <p>]`.'
& $nl ""
& $nl ("Tasks (" + $files.Count + "): " + (($files | ForEach-Object { '`' + [System.IO.Path]::GetFileNameWithoutExtension($_.Name) + '`' }) -join ", ") + ".")
& $nl ""
& $nl '_Generated from the task headers by `tools/gen-builtin-task-reference.ps1`._'
& $nl ""

foreach ($f in $files) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
    # Leading comment block = consecutive //-lines from the top.
    $body = [System.Collections.Generic.List[string]]::new()
    foreach ($l in (Get-Content -LiteralPath $f.FullName)) {
        if ($l -match '^\s*//') { $body.Add(($l -replace '^\s*// ?', '')) }
        else { break }
    }
    # Drop the "Built-in task: <name>" first line (the heading covers it).
    if ($body.Count -gt 0 -and $body[0] -match '^Built-in task:') { $body.RemoveAt(0) }
    & $nl ("## " + $name)
    & $nl ""
    & $nl '```text'
    foreach ($b in $body) { [void]$sb.AppendLine($b.TrimEnd()) }
    & $nl '```'
    & $nl ""
}

$out = Join-Path $root "docs/builtin-tasks.md"
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))
Write-Host ("Wrote {0} ({1} tasks)" -f $out, $files.Count)
