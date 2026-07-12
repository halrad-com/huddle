@echo off
REM build-restart.cmd [huddlePid] - rebuild + relaunch helper for the huddle `reload` command.
REM
REM Spawned detached by huddle, which then exits ITSELF gracefully (its `quit` path:
REM disposes orchestrator + IPC, leaves child claude sessions running). This helper waits
REM for huddle to exit so the publish\huddle.exe lock releases, rebuilds, then launches a
REM fresh instance. No taskkill - huddle stops itself.
REM
REM Can also be run standalone, but quit huddle yourself first (no PID = no wait).

set HPID=%~1

if not "%HPID%"=="" (
    echo [build-restart] waiting for huddle ^(pid %HPID%^) to exit...
    :waitloop
    tasklist /FI "PID eq %HPID%" 2>nul | find "%HPID%" >nul
    if not errorlevel 1 (
        timeout /t 1 /nobreak >nul
        goto waitloop
    )
    echo [build-restart] huddle exited.
)

REM Grace for the file lock to fully release.
timeout /t 1 /nobreak >nul

echo [build-restart] building ^(Release -^> publish^)...
dotnet publish "%~dp0src\huddle.csproj" -c Release -o "%~dp0publish"
if errorlevel 1 (
    echo [build-restart] BUILD FAILED - not relaunching. Press any key to close.
    pause >nul
    exit /b 1
)

echo [build-restart] launching fresh huddle...
pushd "%~dp0"
start "huddle" "%~dp0publish\huddle.exe"
popd
echo [build-restart] done.
