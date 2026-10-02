@echo off
REM ============================================================
REM  RAFFAELLO SERVER - one-time setup on the always-on server PC / VM
REM  Run as Administrator. Needs: PostgreSQL 16 installed (postgres password known),
REM  and either the .NET 8 SDK (to build) or a ready "server-publish" folder next to this file.
REM  Writes everything to setup_server_log.txt
REM ============================================================
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
set LOG=%~dp0setup_server_log.txt
set TARGET=C:\RaffaelloServer
set PORT=5180
set SERVICE=RaffaelloServer
echo RAFFAELLO SERVER SETUP %DATE% %TIME% > "%LOG%"

net session >nul 2>&1
if errorlevel 1 (
  echo Please right-click SETUP_SERVER.bat and choose "Run as administrator".
  pause
  exit /b 1
)

REM ---- 1. find PostgreSQL (psql.exe)
set PGBIN=
for /d %%D in ("%ProgramFiles%\PostgreSQL\*") do if exist "%%D\bin\psql.exe" set PGBIN=%%D\bin
if "%PGBIN%"=="" (
  echo PostgreSQL was not found in "%ProgramFiles%\PostgreSQL". Install PostgreSQL 16 first:
  echo   https://www.postgresql.org/download/windows/   ^(keep the default port 5432, remember the postgres password^)
  pause
  exit /b 1
)
echo [1/8] PostgreSQL found: %PGBIN%
echo PostgreSQL: %PGBIN% >> "%LOG%"

REM ---- 2. build or copy the server
echo [2/8] Preparing the server program in %TARGET% ...
sc query %SERVICE% >nul 2>&1 && sc stop %SERVICE% >nul 2>&1 && timeout /t 5 /nobreak >nul
if exist "%~dp0server-publish\Raffaello.Server.exe" (
  xcopy /e /y /i /q "%~dp0server-publish" "%TARGET%" >> "%LOG%" 2>&1 || goto :fail
) else (
  dotnet --version >nul 2>&1
  if errorlevel 1 (
    echo Neither a server-publish folder nor the .NET 8 SDK was found. Install the .NET 8 SDK or copy server-publish next to this file.
    pause
    exit /b 1
  )
  dotnet publish src\Raffaello.Server\Raffaello.Server.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o "%TARGET%" >> "%LOG%" 2>&1 || goto :fail
)

REM ---- 3. database role + settings (only the first time)
if exist "%TARGET%\appsettings.Local.json" (
  echo [3/8] Keeping the existing settings in %TARGET%\appsettings.Local.json
  goto :migrate
)
echo [3/8] Database account and folders
set /p PGSUPER=Password of the PostgreSQL "postgres" user:
set /p DBPASS=Choose a password for the new "raffaello" database user:
set DOCS=
set /p DOCS=Shared folder for documents (UNC, e.g. \\FILESERVER\RAFFLES\RaffaelloDocs) [Enter = %TARGET%\Documents]:
set BACKUPS=
set /p BACKUPS=Folder for nightly backups (preferably another disk or share) [Enter = %TARGET%\Backups]:
set PGPASSWORD=%PGSUPER%
"%PGBIN%\psql.exe" -U postgres -h localhost -tAc "SELECT 1 FROM pg_roles WHERE rolname='raffaello'" > "%TEMP%\raff_role.txt" 2>> "%LOG%" || goto :fail
findstr /b "1" "%TEMP%\raff_role.txt" >nul
if errorlevel 1 (
  "%PGBIN%\psql.exe" -U postgres -h localhost -c "CREATE ROLE raffaello LOGIN CREATEDB PASSWORD '%DBPASS%'" >> "%LOG%" 2>&1 || goto :fail
) else (
  "%PGBIN%\psql.exe" -U postgres -h localhost -c "ALTER ROLE raffaello WITH LOGIN CREATEDB PASSWORD '%DBPASS%'" >> "%LOG%" 2>&1 || goto :fail
)
set PGPASSWORD=
if "%DOCS%"=="" set DOCS=%TARGET%\Documents
if "%BACKUPS%"=="" set BACKUPS=%TARGET%\Backups
set DOCS_JSON=%DOCS:\=\\%
set BACKUPS_JSON=%BACKUPS:\=\\%
set PGBIN_JSON=%PGBIN:\=\\%
(
  echo {
  echo   "ConnectionStrings": { "Raffaello": "Host=localhost;Port=5432;Database=raffaello;Username=raffaello;Password=%DBPASS%" },
  echo   "Raffaello": {
  echo     "Urls": "http://0.0.0.0:%PORT%",
  echo     "DocumentsRoot": "!DOCS_JSON!",
  echo     "BackupFolder": "!BACKUPS_JSON!",
  echo     "PgBinPath": "!PGBIN_JSON!"
  echo   }
  echo }
) > "%TARGET%\appsettings.Local.json"
icacls "%TARGET%\appsettings.Local.json" /inheritance:r /grant:r "Administrators:F" "SYSTEM:F" >> "%LOG%" 2>&1

:migrate
REM ---- 4. create / upgrade the database
echo [4/8] Creating / upgrading the database ...
"%TARGET%\Raffaello.Server.exe" migrate >> "%LOG%" 2>&1 || goto :fail

REM ---- 5. first ADMIN account
"%TARGET%\Raffaello.Server.exe" users > "%TEMP%\raff_users.txt" 2>> "%LOG%"
findstr /c:"ADMIN" "%TEMP%\raff_users.txt" >nul
if errorlevel 1 (
  echo [5/8] Create the first ADMIN account ^(manages users, roles and settings^)
  set /p ADMINUSER=Admin user name:
  set /p ADMINPASS=Admin password ^(8+ characters^):
  "%TARGET%\Raffaello.Server.exe" adduser "!ADMINUSER!" ADMIN "!ADMINPASS!" --windows "%USERDOMAIN%\!ADMINUSER!" >> "%LOG%" 2>&1 || goto :fail
) else (
  echo [5/8] An ADMIN account already exists
)

REM ---- 6. Windows service (auto start, restart on failure)
echo [6/8] Installing the Windows service %SERVICE% ...
sc query %SERVICE% >nul 2>&1
if errorlevel 1 (
  sc create %SERVICE% binPath= "\"%TARGET%\Raffaello.Server.exe\"" start= delayed-auto DisplayName= "Raffaello Server" >> "%LOG%" 2>&1 || goto :fail
  sc description %SERVICE% "Raffaello multi-user server (API + live updates) for the Raffles electrical QS app" >> "%LOG%" 2>&1
)
sc failure %SERVICE% reset= 86400 actions= restart/60000/restart/60000/restart/60000 >> "%LOG%" 2>&1
echo     If documents are on a network share, set the service to run as a domain account with access to it:
echo     services.msc ^> Raffaello Server ^> Log On ^> This account. ^(LocalSystem cannot open \\server\share paths.^)

REM ---- 7. firewall
echo [7/8] Opening TCP port %PORT% in the Windows firewall (domain + private networks) ...
netsh advfirewall firewall delete rule name="Raffaello Server" >nul 2>&1
netsh advfirewall firewall add rule name="Raffaello Server" dir=in action=allow protocol=TCP localport=%PORT% profile=domain,private >> "%LOG%" 2>&1 || goto :fail

REM ---- 8. start + address
sc start %SERVICE% >> "%LOG%" 2>&1
timeout /t 5 /nobreak >nul
echo [8/8] Done. Users type ONE of these addresses in Raffaello ^> Settings ^> Data source ^> Server:
"%TARGET%\Raffaello.Server.exe" url
echo.
echo Backups: every night at 02:00 (Raffaello.Server.exe backup / restore FILE). Log: %LOG%
echo SETUP OK >> "%LOG%"
pause
exit /b 0

:fail
echo.
echo SETUP FAILED - see setup_server_log.txt (last lines below)
echo SETUP FAILED >> "%LOG%"
powershell -NoProfile -Command "Get-Content -Path '%LOG%' -Tail 30"
pause
exit /b 1
