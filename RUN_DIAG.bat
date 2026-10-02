@echo off
REM ============================================================
REM  RAFFAELLO - start-up diagnostics. Starts publish\Raffaello.exe, waits 25 s and writes diag_log.txt
REM  (start-up steps, error.log, running processes, Windows crash events). Send diag_log.txt.
REM ============================================================
setlocal
cd /d "%~dp0"
set OUT=%~dp0diag_log.txt
set RAF=%APPDATA%\Raffaello
echo RAFFAELLO DIAG %DATE% %TIME% > "%OUT%"
if not exist "publish\Raffaello.exe" (
  echo publish\Raffaello.exe not found - run RUN_ONE_CLICK.bat first >> "%OUT%"
  echo publish\Raffaello.exe not found - run RUN_ONE_CLICK.bat first
  pause
  exit /b 1
)
echo Starting Raffaello... (waiting 25 seconds)
start "" "%~dp0publish\Raffaello.exe"
timeout /t 25 /nobreak > nul

echo. >> "%OUT%"
echo ===== RUNNING PROCESSES ===== >> "%OUT%"
tasklist /FI "IMAGENAME eq Raffaello.exe" >> "%OUT%" 2>&1
echo. >> "%OUT%"
echo ===== %RAF%\startup.log ===== >> "%OUT%"
if exist "%RAF%\startup.log" (type "%RAF%\startup.log" >> "%OUT%") else (echo NOT CREATED - the app stopped before its first line of code >> "%OUT%")
echo. >> "%OUT%"
echo ===== %RAF%\error.log (last 150 lines) ===== >> "%OUT%"
if exist "%RAF%\error.log" (powershell -NoProfile -Command "Get-Content -Path '%RAF%\error.log' -Tail 150" >> "%OUT%" 2>&1) else (echo none >> "%OUT%")
echo. >> "%OUT%"
echo ===== %RAF%\binding_errors.log (first 200 lines) ===== >> "%OUT%"
if exist "%RAF%\binding_errors.log" (powershell -NoProfile -Command "Get-Content -Path '%RAF%\binding_errors.log' -TotalCount 200" >> "%OUT%" 2>&1) else (echo none >> "%OUT%")
echo. >> "%OUT%"
echo ===== WINDOWS APPLICATION EVENTS (last hour, .NET Runtime / Application Error) ===== >> "%OUT%"
powershell -NoProfile -Command "Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddHours(-1)} -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -in '.NET Runtime','Application Error','Windows Error Reporting' -and $_.Message -match 'Raffaello' } | Select-Object -First 5 | ForEach-Object { '----- ' + $_.TimeCreated + ' ' + $_.ProviderName; $_.Message }" >> "%OUT%" 2>&1
echo. >> "%OUT%"
echo ===== publish folder ===== >> "%OUT%"
dir /b "publish" >> "%OUT%" 2>&1

echo Done. Send diag_log.txt
start "" notepad "%OUT%"
pause
