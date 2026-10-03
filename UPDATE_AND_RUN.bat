@echo off
REM ==================================================================================================
REM  RAFFAELLO - UPDATE (PATCH) + RUN.  Put this file next to the "code" folder (e.g. Desktop\RAFFAELLO).
REM  Downloads ONLY the files that changed since your version, quick build, starts the app.
REM  All the work is done by code\tools\update.ps1 (that script is itself updated by every patch).
REM  The app does the same from SETTINGS > UPDATES > UPDATE NOW. Log: update_log.txt next to this file.
REM  Everything below is on ONE line on purpose: the updater may refresh this file while it runs.
REM ==================================================================================================
set RAFFAELLO_FROM_BAT=1& (if not exist "%~dp0code\tools\update.ps1" powershell -NoProfile -Command "[Net.ServicePointManager]::SecurityProtocol='Tls12'; New-Item -ItemType Directory -Force '%~dp0code\tools' | Out-Null; Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/jdezonia1/7amo/claude/dazzling-turing-20kq62/tools/update.ps1' -OutFile '%~dp0code\tools\update.ps1'") & powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0code\tools\update.ps1" -Root "%~dp0." & exit /b
