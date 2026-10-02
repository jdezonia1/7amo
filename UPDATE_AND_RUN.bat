@echo off
REM ============================================================
REM  RAFFAELLO - FAST UPDATE + RUN (about 1 minute instead of 5+)
REM  Put this file in its own folder (e.g. Desktop\RAFFAELLO) and double-click it after every fix.
REM  1) downloads the latest code from GitHub   2) quick build, no tests, no publish   3) starts the app
REM  Writes update_log.txt (send it if the build fails). Use RUN_ONE_CLICK.bat only for a full release build.
REM ============================================================
setlocal
set BRANCH=claude/dazzling-turing-20kq62
set ZIPURL=https://github.com/jdezonia1/7amo/archive/refs/heads/%BRANCH%.zip
cd /d "%~dp0"
set HERE=%~dp0
set CODE=%HERE%code
set LOG=%HERE%update_log.txt
set TMPZIP=%TEMP%\raffaello_update.zip
set TMPDIR=%TEMP%\raffaello_update
echo RAFFAELLO FAST UPDATE %DATE% %TIME% > "%LOG%"

echo [1/4] Closing Raffaello if it is open...
taskkill /IM Raffaello.exe /F > nul 2>&1

echo [2/4] Downloading the latest version...
if exist "%TMPDIR%" rmdir /S /Q "%TMPDIR%"
powershell -NoProfile -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol='Tls12'; Invoke-WebRequest -Uri '%ZIPURL%' -OutFile '%TMPZIP%'; Expand-Archive -Path '%TMPZIP%' -DestinationPath '%TMPDIR%' -Force" >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
for /D %%D in ("%TMPDIR%\*") do set SRC=%%D
REM keep bin / obj so the build is incremental (seconds, not minutes)
robocopy "%SRC%" "%CODE%" /MIR /XD bin obj publish server-publish out /XF build_log.txt diag_log.txt /NFL /NDL /NJH /NJS /NP >> "%LOG%" 2>&1
if errorlevel 8 goto :fail
for /f "delims=" %%C in ('powershell -NoProfile -Command "(Invoke-RestMethod -Uri 'https://api.github.com/repos/jdezonia1/7amo/commits/%BRANCH%').commit.message.Split([char]10)[0]" 2^>nul') do echo      Latest change: %%C
echo.

echo [3/4] Building (quick, no tests)...
dotnet build "%CODE%\src\Raffaello.App\Raffaello.App.csproj" -c Debug -nologo -v q >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

echo [4/4] Starting Raffaello...
start "" "%CODE%\src\Raffaello.App\bin\Debug\net8.0-windows10.0.19041.0\Raffaello.exe"
echo UPDATE OK >> "%LOG%"
echo Done. If something looks wrong: screenshot + run code\RUN_DIAG.bat (send diag_log.txt).
timeout /t 3 > nul
exit /b 0

:fail
echo.
echo UPDATE FAILED - send update_log.txt (last lines below)
echo UPDATE FAILED >> "%LOG%"
powershell -NoProfile -Command "Get-Content -Path '%LOG%' -Tail 25"
pause
exit /b 1
