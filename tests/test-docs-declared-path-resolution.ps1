<#
  Regression capture test — declared-doc path resolution.

  A declared doc link in a scratchpad may be written relative to the SCRATCHPAD FILE
  (standard markdown, e.g. ../../repo/docs/x.json) or relative to the REPO ROOT
  (docs/x.json). Resolution must find the real file for BOTH; the old code resolved
  relative paths against the repo root only, so a markdown-relative link landed at a
  bogus path and `open` failed.

  Verified via the file's distinctive mtime: `docs` shows a declared doc's timestamp
  from the RESOLVED file when it exists, or the scratchpad's mtime when it doesn't. So a
  correctly-resolved doc shows its own fixed date; a mis-resolved one shows "today".

  Run:  powershell -ExecutionPolicy Bypass -File tests/test-docs-declared-path-resolution.ps1
  Exit: 0 = pass, 1 = fail.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$fails = @()

& dotnet build "$repo/src/huddle.csproj" -c Debug -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: build failed"; exit 1 }
$exe = Get-ChildItem -Path "$repo/src/bin/Debug" -Recurse -Filter huddle.exe | Select-Object -First 1
if (-not $exe) { Write-Host "FAIL: huddle.exe not found"; exit 1 }

$fix = Join-Path ([System.IO.Path]::GetTempPath()) ("huddle-declpath-" + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $fix 'testrepo/docs/reference') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $fix 'logs/testrepo_researcher') | Out-Null

    # Two real docs (.json so auto-discovery, which is *.md only, ignores them — isolating the
    # test to DECLARED-path resolution). Distinctive fixed mtimes.
    $relFile  = Join-Path $fix 'testrepo/docs/reference/relfile.json'
    $repoFile = Join-Path $fix 'testrepo/docs/repofile.json'
    '{"x":1}' | Set-Content -Path $relFile  -Encoding UTF8
    '{"x":2}' | Set-Content -Path $repoFile -Encoding UTF8
    (Get-Item $relFile).LastWriteTime  = [datetime]'2021-06-07 08:09:00'
    (Get-Item $repoFile).LastWriteTime = [datetime]'2022-03-04 05:06:00'

    # Scratchpad declares one doc-relative (../../) link and one repo-relative link.
    $sp = @(
        '# Test scratchpad',
        '',
        '## Documents',
        '',
        '- [Rel Doc](../../testrepo/docs/reference/relfile.json) - doc-relative link #output',
        '- [Repo Doc](docs/repofile.json) - repo-relative link #output'
    ) -join "`r`n"
    $sp | Set-Content -Path (Join-Path $fix 'logs/testrepo_researcher/scratchpad.md') -Encoding UTF8

    $cfg = @{
        sessions    = @(@{ name = 'testrepo'; root = (Join-Path $fix 'testrepo'); purpose = 'test' })
        ipc         = $false
        contextFile = $false
        claudePath  = 'C:\Windows\System32\cmd.exe'
    } | ConvertTo-Json -Depth 5
    $cfg | Set-Content -Path (Join-Path $fix 'huddle.json') -Encoding UTF8

    $out = @('docs @testrepo','quit') | & $exe.FullName --config (Join-Path $fix 'huddle.json') 2>&1 | Out-String

    if (-not $out.Contains('2021-06-07 08:09')) {
        $fails += "doc-relative (../../) link did not resolve to the real file (open would fail)"
    }
    if (-not $out.Contains('2022-03-04 05:06')) {
        $fails += "repo-relative link no longer resolves (regression in the common case)"
    }
}
finally {
    if (Test-Path $fix) { Remove-Item -Recurse -Force $fix -ErrorAction SilentlyContinue }
}

if ($fails.Count -eq 0) { Write-Host "PASS: declared-doc path resolution (doc-relative + repo-relative)"; exit 0 }
else { Write-Host "FAIL: declared-doc path resolution"; $fails | ForEach-Object { Write-Host "  - $_" }; exit 1 }
