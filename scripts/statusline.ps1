# scripts/statusline.ps1
# Claude Code status line: [label] branch | model | ctx:N%
#
# LABEL RESOLUTION (in order of priority):
#   1. CLAUDE_SESSION_LABEL env var — used literally (set by huddle at launch)
#   2. session_name from Claude Code JSON
#   3. CLAUDE_PERSONA env var, composed with repo basename as "repo:persona"
#   4. Bare repo basename, no persona suffix
#
# INSTALL:
#   Copy this file to ~/.claude/statusline.ps1 and set in settings.json:
#     "statusLine": {
#       "type": "command",
#       "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:/Users/<you>/.claude/statusline.ps1"
#     }

$ErrorActionPreference = 'SilentlyContinue'

# --- Read JSON from stdin ---
$raw = [Console]::In.ReadToEnd()
try {
    $j = $raw | ConvertFrom-Json
} catch {
    $j = $null
}

# --- cwd / repo ---
$cwd = ''
if ($j) {
    if ($j.workspace -and $j.workspace.current_dir) {
        $cwd = [string]$j.workspace.current_dir
    } elseif ($j.cwd) {
        $cwd = [string]$j.cwd
    }
}
if ($cwd) {
    $repo = Split-Path -Path $cwd -Leaf
} else {
    $repo = '?'
}

# --- label: CLAUDE_SESSION_LABEL > session_name > CLAUDE_PERSONA composed > bare repo ---
$sessionName = ''
if ($j -and $j.session_name) { $sessionName = [string]$j.session_name }
if ($env:CLAUDE_SESSION_LABEL) {
    $label = $env:CLAUDE_SESSION_LABEL
} elseif ($sessionName) {
    $label = $sessionName
} elseif ($env:CLAUDE_PERSONA) {
    $label = "$($repo):$($env:CLAUDE_PERSONA)"
} else {
    $label = $repo
}

# --- git branch (best-effort) ---
$branch = ''
if ($cwd -and (Test-Path -LiteralPath $cwd)) {
    $env:GIT_OPTIONAL_LOCKS = '0'
    $b = (& git -C $cwd symbolic-ref --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $b) { $branch = ($b | Select-Object -First 1).Trim() }
}

# --- model display ---
$model = ''
if ($j -and $j.model) {
    if ($j.model.display_name) { $model = [string]$j.model.display_name }
    elseif ($j.model.id) { $model = [string]$j.model.id }
}

# --- context remaining % ---
$remaining = $null
if ($j -and $j.context_window -and ($null -ne $j.context_window.remaining_percentage)) {
    $remaining = [int][math]::Round([double]$j.context_window.remaining_percentage)
}

# --- Assemble ANSI-colored output ---
$ESC = [char]27
$out = "$ESC[1m[$label]$ESC[0m"

if ($branch) {
    $out += " $ESC[36m$branch$ESC[0m"
}

if ($model) {
    $short = $model -replace '^Claude ', ''
    $out += " | $short"
}

if ($null -ne $remaining) {
    if     ($remaining -gt 50) { $color = "$ESC[32m" }
    elseif ($remaining -gt 20) { $color = "$ESC[33m" }
    else                       { $color = "$ESC[31m" }
    $out += " | ctx:$color$remaining%$ESC[0m"
}

[Console]::Out.Write($out)
