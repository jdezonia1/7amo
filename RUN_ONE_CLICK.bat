@echo off
REM ============================================================
REM  RAFFAELLO - one click: restore, build, ALL tests, publish app + server, launch
REM  Needs the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
REM  Writes everything to build_log.txt (send this file if it fails)
REM ============================================================
setlocal
cd /d "%~dp0"
set LOG=%~dp0build_log.txt
echo RAFFAELLO BUILD %DATE% %TIME% > "%LOG%"
dotnet --info >> "%LOG%" 2>&1
if errorlevel 1 (
  echo .NET SDK not found. Install the .NET 8 SDK and run again.
  echo .NET SDK not found >> "%LOG%"
  pause
  exit /b 1
)

echo [1/5] Restoring packages...
dotnet restore Raffaello.sln >> "%LOG%" 2>&1 || goto :fail

echo [2/5] Building (Release)...
dotnet build Raffaello.sln -c Release --no-restore >> "%LOG%" 2>&1 || goto :fail

echo [3/5] Running tests (all projects run; failures are listed at the end)...
set TESTFAIL=0
dotnet test tests\Raffaello.Core.Tests\Raffaello.Core.Tests.csproj -c Release --no-build >> "%LOG%" 2>&1 || set TESTFAIL=1
REM [phase5] begin: server tests (PostgreSQL integration tests skip themselves when no test database is reachable)
dotnet test tests\Raffaello.Server.Tests\Raffaello.Server.Tests.csproj -c Release --no-build >> "%LOG%" 2>&1 || set TESTFAIL=1
REM [phase5] end
REM [phase6] Aconex browser tests against the local mock site (use Microsoft Edge; skipped when no browser can start)
dotnet test tests\Raffaello.Automation.Tests\Raffaello.Automation.Tests.csproj -c Release --no-build >> "%LOG%" 2>&1 || set TESTFAIL=1
REM smart document reader: offline OCR stack (PaddleOCR + OpenCV + PDFium) on synthetic pages
dotnet test tests\Raffaello.Ocr.Tests\Raffaello.Ocr.Tests.csproj -c Release --no-build >> "%LOG%" 2>&1 || set TESTFAIL=1
if "%TESTFAIL%"=="1" (
  echo.
  echo FAILED TESTS:
  findstr /C:"[FAIL]" /C:"Failed!" /C:"Passed!" "%LOG%"
  goto :fail
)

echo [4/5] Publishing self-contained win-x64 single file to publish\ ...
dotnet publish src\Raffaello.App\Raffaello.App.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None ^
  -o publish >> "%LOG%" 2>&1 || goto :fail

REM [phase5] begin: server build for the office server PC (copy server-publish + SETUP_SERVER.bat there)
echo      Publishing Raffaello.Server to server-publish\ ...
dotnet publish src\Raffaello.Server\Raffaello.Server.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o server-publish >> "%LOG%" 2>&1 || goto :fail
REM [phase5] end

REM [phase6] the Playwright driver (.playwright\node) must sit next to Raffaello.exe for the Aconex automation
if not exist "publish\.playwright\node" (
  echo WARNING: publish\.playwright\node missing - Aconex automation will not start >> "%LOG%"
  echo WARNING: Playwright driver not found next to Raffaello.exe - see build_log.txt
)
echo      Tests and publish finished: %DATE% %TIME% >> "%LOG%"

echo [5/5] Launching Raffaello...
echo BUILD OK >> "%LOG%"
start "" "%~dp0publish\Raffaello.exe"
echo Done. Log: %LOG%
exit /b 0

:fail
echo.
echo BUILD FAILED - see build_log.txt (last lines below)
echo BUILD FAILED >> "%LOG%"
powershell -NoProfile -Command "Get-Content -Path '%LOG%' -Tail 30"
pause
exit /b 1
