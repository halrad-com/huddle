<#
  demo-project-status.ps1 - end-to-end demo of the projects status page WITH the
  worktree fix.

  It proves the "pre/post merge" behavior: a project authored on a feature branch lives
  only in a LINKED git worktree until it merges. huddle must surface it there and then.

  What the script does (all in a throwaway temp dir, nothing touches your real repos):
    1. builds huddle
    2. makes a git repo with a project 'alpha' committed on main
    3. adds a linked worktree on branch 'feature' with a project 'beta' that exists
       ONLY in that worktree (never committed to main = pre-merge)
    4. registers the MAIN repo (not the worktree) in a demo huddle.json
    5. runs:  huddle --projects-html <out> --config <demo huddle.json>
    6. asserts the report contains BOTH alpha (main) AND beta (feature worktree)
       -> beta only shows if worktree discovery works

  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\demo-project-status.ps1
#>

$ErrorActionPreference = 'Stop'

function Say([string]$m) { Write-Host $m }
function Fail([string]$m) { Write-Host "FAIL: $m" -ForegroundColor Red; exit 1 }

# --- locate the repo + build huddle ----------------------------------------------------
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Csproj   = Join-Path $RepoRoot 'src\huddle.csproj'
if (-not (Test-Path $Csproj)) { Fail "cannot find $Csproj" }

Say "Building huddle..."
& dotnet build $Csproj -c Debug --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed (exit $LASTEXITCODE)" }

# The csproj targets a RID, so output lands under a win-x64 subdir. Find it robustly.
$BinRoot = Join-Path $RepoRoot 'src\bin\Debug\net8.0'
$Exe = Get-ChildItem -Path $BinRoot -Recurse -Filter 'huddle.exe' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
$Dll = Get-ChildItem -Path $BinRoot -Recurse -Filter 'huddle.dll' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $Exe -and -not $Dll) { Fail "huddle build output not found under $BinRoot" }

# --- throwaway fixture: repo with a linked worktree ------------------------------------
$Stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$Demo    = Join-Path $env:TEMP "huddle-demo-$Stamp"
$Main    = Join-Path $Demo 'repo'
$Feature = Join-Path $Demo 'repo-feature'
New-Item -ItemType Directory -Force -Path $Main | Out-Null

Say "Fixture: $Demo"

# minimal frontmatter project.md the projects lens understands
function Write-Project([string]$dir, [string]$slug, [string]$title, [string]$goal) {
    $pdir = Join-Path $dir "docs\projects\$slug"
    New-Item -ItemType Directory -Force -Path $pdir | Out-Null
    $body = @"
---
slug: $slug
title: $title
goal: $goal
status: active
---
# $title

$goal
"@
    Set-Content -Path (Join-Path $pdir 'project.md') -Value $body -Encoding UTF8
}

# main branch: 'alpha' is committed
& git -C $Main init -q
& git -C $Main config user.email 'demo@huddle.local'
& git -C $Main config user.name  'huddle demo'
Write-Project $Main 'alpha' 'Alpha (on main)' 'A project already merged to main.'
& git -C $Main add -A
& git -C $Main commit -q -m 'alpha project on main'

# feature branch in a LINKED worktree: 'beta' exists only here (pre-merge)
& git -C $Main worktree add -q $Feature -b feature
if ($LASTEXITCODE -ne 0) { Fail "git worktree add failed (exit $LASTEXITCODE)" }
Write-Project $Feature 'beta' 'Beta (feature worktree, pre-merge)' 'A project that lives only in the linked worktree until it merges.'

# --- demo huddle.json: register the MAIN repo only -------------------------------------
$DemoJson = Join-Path $Demo 'huddle.json'
$MainJson = ($Main -replace '\\','\\')   # escape backslashes for JSON
$cfg = @"
{
  "sessions": [
    { "name": "demo", "root": "$MainJson", "purpose": "worktree demo", "autoStart": false }
  ]
}
"@
Set-Content -Path $DemoJson -Value $cfg -Encoding UTF8

# --- render the projects status page headlessly ---------------------------------------
$Out = Join-Path $Demo 'projects-report.html'
Say "Rendering projects report..."
if (Test-Path $Exe) {
    & $Exe --projects-html $Out --config $DemoJson
} else {
    & dotnet $Dll --projects-html $Out --config $DemoJson
}
if ($LASTEXITCODE -ne 0) { Fail "huddle --projects-html failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $Out)) { Fail "no report written" }

# --- verify: BOTH projects present; beta proves the worktree fix -----------------------
$html = Get-Content -Raw -Path $Out
$hasAlpha = $html -match 'Alpha \(on main\)'
$hasBeta  = $html -match 'Beta \(feature worktree'

Say ""
Say "Report: $Out"
Say ("  alpha (main worktree)      : " + $(if ($hasAlpha) { 'FOUND' } else { 'MISSING' }))
Say ("  beta  (feature worktree)   : " + $(if ($hasBeta)  { 'FOUND' } else { 'MISSING' }))
Say ""

if ($hasAlpha -and $hasBeta) {
    Write-Host "PASS - projects status page includes the pre-merge worktree project." -ForegroundColor Green
    try { Invoke-Item $Out } catch { }
    Say "Fixture kept for inspection: $Demo"
    exit 0
} else {
    Fail "worktree project not discovered (alpha=$hasAlpha beta=$hasBeta)"
}
