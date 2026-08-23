@echo off
REM Riddled -- double-click this to refresh guild data, then start WoW.
REM
REM Put a shortcut to this on your desktop or taskbar and use it instead of
REM the Battle.net launcher. If the refresh fails for any reason the game
REM still starts; you just get slightly older item levels.
REM
REM -ExecutionPolicy Bypass applies to this one run only. It does not change
REM any machine setting.

setlocal

set "SCRIPT=%~dp0Update-Riddled.ps1"

if not exist "%SCRIPT%" (
    echo Could not find Update-Riddled.ps1 next to this file.
    echo Keep Play-Riddled.cmd and Update-Riddled.ps1 in the same folder.
    pause
    exit /b 1
)

REM Prefer PowerShell 7 if it is installed, otherwise use the built-in 5.1.
where /q pwsh.exe
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Launch %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Launch %*
)

endlocal
