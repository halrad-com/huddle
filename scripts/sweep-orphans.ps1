<#  Sweep for leaked session resources (B016, docs/resource-ledger-spec.md).
    Default: report only.  -Kill: reclaim (taskkill + delete artifacts).  #>
param([switch]$Kill)

$root = Split-Path $PSScriptRoot -Parent
$ledgerDir = Join-Path $root "ipc\resledger"
$leaks = @()

# 1. Ledger-declared resources never marked cleaned
if (Test-Path $ledgerDir) {
    foreach ($f in Get-ChildItem $ledgerDir -Filter *.json) {
        try { $doc = Get-Content $f.FullName -Raw | ConvertFrom-Json }
        catch { Write-Host "WARN: malformed ledger $($f.Name): $_"; continue }
        foreach ($r in $doc.resources) {
            if ($r.cleanedAt) { continue }
            $alive = $r.pid -and (Get-Process -Id $r.pid -ErrorAction SilentlyContinue)
            $leaks += [pscustomobject]@{
                Source = $f.Name; Session = $doc.session; Id = $r.id
                ProcId = $r.pid; Alive = [bool]$alive; What = $r.what; Cleanup = $r.cleanup
                Artifacts = (@($r.artifacts) -join "; ")
            }
        }
    }
}

# 2. Heuristic: headless browsers pointing at Temp\claude paths, not in any ledger
$heur = Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -match 'headless' -and $_.CommandLine -match '[/\\]Temp[/\\]claude[/\\]' } |
    Where-Object { $_.CommandLine -notmatch '--type=' }   # browser parents only, not helper children
foreach ($p in $heur) {
    if ($leaks | Where-Object { $_.ProcId -eq $p.ProcessId }) { continue }
    $leaks += [pscustomobject]@{
        Source = "(heuristic)"; Session = "?"; Id = "unregistered-headless"
        ProcId = $p.ProcessId; Alive = $true
        What = $p.CommandLine.Substring(0, [Math]::Min(120, $p.CommandLine.Length))
        Cleanup = "taskkill /PID $($p.ProcessId) /T /F"; Artifacts = ""
    }
}

if (-not $leaks) { Write-Host "No leaked resources found."; exit 0 }
$leaks | Format-Table Source, Session, Id, ProcId, Alive, What -AutoSize | Out-String -Width 220 | Write-Host

if ($Kill) {
    foreach ($l in $leaks | Where-Object Alive) {
        Write-Host "reclaiming pid $($l.ProcId): $($l.Cleanup)"
        taskkill /PID $l.ProcId /T /F 2>$null
    }
    Start-Sleep -Seconds 1
    foreach ($l in $leaks | Where-Object { $_.Artifacts }) {
        foreach ($a in $l.Artifacts -split "; ") {
            if ($a -and (Test-Path $a)) { Write-Host "deleting $a"; Remove-Item -Recurse -Force $a }
        }
    }
} else {
    Write-Host "`nReport-only. Re-run with -Kill to reclaim."
}
