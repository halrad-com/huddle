<#
  Regression capture test — docs auto-discovery resilience.

  Directory.GetFiles(docsDir, "*.md", AllDirectories) throws if a subdirectory is
  removed/renamed mid-walk or points at a broken reparse target; the old code caught
  that and dropped the ENTIRE repo's docs for that run, so a doc on disk could vanish
  from one `docs` run and reappear on the next. The resilient walk must skip only the
  bad directory and still surface siblings.

  This reproduces the throw deterministically with a broken directory junction beside a
  real spec, then asserts the spec still shows in `docs`.

  Run:  powershell -ExecutionPolicy Bypass -File tests/test-docs-scan-resilience.ps1
  Exit: 0 = pass, 1 = fail.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$fails = @()

& dotnet build "$repo/src/huddle.csproj" -c Debug -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: build failed"; exit 1 }
$exe = Get-ChildItem -Path "$repo/src/bin/Debug" -Recurse -Filter huddle.exe | Select-Object -First 1
if (-not $exe) { Write-Host "FAIL: huddle.exe not found"; exit 1 }

$fix = Join-Path ([System.IO.Path]::GetTempPath()) ("huddle-docsscan-" + [guid]::NewGuid().ToString('N'))
$junction = Join-Path $fix 'testrepo/docs/loop'
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $fix 'testrepo/docs/specs') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $fix 'logs') | Out-Null
    "# Good Spec Survives`r`n`r`nbody" | Set-Content -Path (Join-Path $fix 'testrepo/docs/specs/good.md') -Encoding UTF8

    # Cyclic junction: docs/loop -> docs. AllDirectories follows it and recurses until the
    # path exceeds the limit -> throws PathTooLongException, and the old code dropped the WHOLE
    # repo's docs on that. The resilient walk skips reparse points, so siblings still enumerate.
    # mklink /J needs no admin.
    $target = Join-Path $fix 'testrepo/docs'
    cmd /c mklink /J "$junction" "$target" | Out-Null
    if (-not (Test-Path $junction)) { Write-Host "SKIP: could not create junction in this environment"; exit 0 }

    $cfg = @{
        sessions    = @(@{ name = 'testrepo'; root = (Join-Path $fix 'testrepo'); purpose = 'test' })
        ipc         = $false
        contextFile = $false
        claudePath  = 'C:\Windows\System32\cmd.exe'
    } | ConvertTo-Json -Depth 5
    $cfg | Set-Content -Path (Join-Path $fix 'huddle.json') -Encoding UTF8

    $out = @('docs @testrepo','quit') | & $exe.FullName --config (Join-Path $fix 'huddle.json') 2>&1 | Out-String

    if (-not $out.Contains('Good Spec Survives')) {
        $fails += "sibling spec was dropped when a cyclic junction sat under docs/ (repo-wide scan failure not isolated)"
    }
}
finally {
    if (Test-Path $junction) { cmd /c rmdir "$junction" | Out-Null }
    if (Test-Path $fix) { Remove-Item -Recurse -Force $fix -ErrorAction SilentlyContinue }
}

if ($fails.Count -eq 0) { Write-Host "PASS: docs scan resilience (bad subdir doesn't drop the repo)"; exit 0 }
else { Write-Host "FAIL: docs scan resilience"; $fails | ForEach-Object { Write-Host "  - $_" }; exit 1 }
