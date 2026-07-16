<#
  Regression capture test — janitor stale-mail section (c5541da).

  Guards the "old business" stale-mail report folded into `janitor`:
    - unprocessed mail for a STOPPED recipient is surfaced as old business,
    - task-type mail is flagged as possible dropped work,
    - malformed mail files are skipped (not crashed on),
    - the report mutates nothing (mail stays in inbox).

  Self-contained: builds huddle, creates an isolated temp config + ipc fixture,
  runs `janitor`, asserts the captured output, then cleans up. Touches no live state.

  Run:  powershell -ExecutionPolicy Bypass -File tests/test-janitor-stale-mail.ps1
  Exit: 0 = pass, 1 = fail.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$fails = @()

# --- Build current code ---
& dotnet build "$repo/src/huddle.csproj" -c Debug -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: build failed"; exit 1 }
$exe = Get-ChildItem -Path "$repo/src/bin/Debug" -Recurse -Filter huddle.exe | Select-Object -First 1
if (-not $exe) { Write-Host "FAIL: huddle.exe not found after build"; exit 1 }

# --- Isolated fixture ---
$fix = Join-Path ([System.IO.Path]::GetTempPath()) ("huddle-janitor-test-" + [guid]::NewGuid().ToString('N'))
$taskFile = Join-Path $fix 'ipc/myapp_backenddev-2/inbox/task1.json'
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $fix 'ipc/myapp_backenddev-2/inbox') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $fix 'ipc/huddle_documenter/inbox') | Out-Null

    '{ "sessions": [], "claudePath": "C:\\Windows\\System32\\cmd.exe", "ipc": true, "contextFile": false }' |
        Set-Content -Path (Join-Path $fix 'huddle.json') -Encoding UTF8
    '{"from":"myapp:architect","to":"myapp:backenddev-2","timestamp":"2026-05-01T22:25:00Z","type":"task","subject":"Pick up Wave 4 remaining work - p/q/r/s/t","body":{}}' |
        Set-Content -Path $taskFile -Encoding UTF8
    '{"from":"myapp:backenddev","to":"myapp:backenddev-2","timestamp":"2026-05-02T05:30:00Z","type":"info","subject":"heads-up: access-gates overlap","body":{}}' |
        Set-Content -Path (Join-Path $fix 'ipc/myapp_backenddev-2/inbox/info1.json') -Encoding UTF8
    '{ this is not valid json - must be skipped, not crash janitor' |
        Set-Content -Path (Join-Path $fix 'ipc/huddle_documenter/inbox/bad.json') -Encoding UTF8

    # --- Run janitor, then quit (quit leaves sessions running; no teardown) ---
    $out = @('janitor','quit') | & $exe.FullName --config (Join-Path $fix 'huddle.json') 2>&1 | Out-String

    # --- Assertions ---
    if (-not $out.Contains('old business'))              { $fails += "stale-mail section header ('old business') missing" }
    if (-not $out.Contains('myapp:backenddev-2'))   { $fails += "stopped recipient not listed" }
    if (-not $out.Contains('Wave 4 remaining work'))     { $fails += "task subject not surfaced" }
    if (-not $out.Contains('may be dropped work'))        { $fails += "task-type not flagged as dropped-work risk" }
    if ($out.Contains('huddle:documenter'))              { $fails += "malformed mail was NOT skipped (documenter appeared)" }
    if (-not (Test-Path $taskFile))                      { $fails += "janitor mutated state - task mail no longer in inbox" }
}
finally {
    if (Test-Path $fix) { Remove-Item -Recurse -Force $fix }
}

if ($fails.Count -eq 0) {
    Write-Host "PASS: janitor stale-mail capture test"
    exit 0
} else {
    Write-Host "FAIL: janitor stale-mail capture test"
    $fails | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
