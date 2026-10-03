<#
  RAFFAELLO PATCH UPDATER
  Downloads ONLY the files that changed on GitHub since the installed version (a patch), not the whole zip.
    1. asks GitHub for the latest commit of the branch
    2. compares it with the installed commit (code\.raffaello_commit) -> changed / added / removed files
    3. downloads just those files, deletes removed ones, shows what changed (commit messages)
    4. quick incremental build (skipped when only docs changed) and starts Raffaello
  Full download only the first time (no installed commit yet), after a history rewrite, or with -Full.
  Your data is never touched (it lives in %APPDATA% / %LOCALAPPDATA% / the shared data file, not in code\).
  Usage: UPDATE_AND_RUN.bat, SETTINGS > UPDATES > UPDATE NOW in the app, or
         powershell -ExecutionPolicy Bypass -File code\tools\update.ps1 -Root <folder that contains code>
#>
param(
    [string]$Root = "",
    [string]$Code = "",
    [string]$Branch = "claude/dazzling-turing-20kq62",
    [string]$Repo = "jdezonia1/7amo",
    [int]$WaitPid = 0,
    [switch]$Full,
    [switch]$NoStart,
    [switch]$CheckOnly
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $Code) { $Code = Split-Path -Parent $PSScriptRoot }   # ...\code\tools -> ...\code
if (-not $Root) { $Root = Split-Path -Parent $Code }            # ...\code -> folder with UPDATE_AND_RUN.bat
$Code = (Resolve-Path $Code).Path; $Root = (Resolve-Path $Root).Path
$Log = Join-Path $Root "update_log.txt"
$State = Join-Path $Code ".raffaello_commit"
$Exe = Join-Path $Code "src\Raffaello.App\bin\Debug\net8.0-windows10.0.19041.0\Raffaello.exe"
$Api = "https://api.github.com/repos/$Repo"
$Headers = @{ "User-Agent" = "Raffaello-Updater"; "Accept" = "application/vnd.github+json" }

function Say([string]$m, [string]$color = "Gray") { Write-Host $m -ForegroundColor $color; Add-Content -Path $Log -Value $m }
function Fail([string]$m) {
    Say ""; Say "UPDATE FAILED: $m" "Red"
    Say "Send update_log.txt ($Log) if this keeps happening." "Red"
    if ($Host.Name -eq "ConsoleHost") { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
function RawUrl([string]$sha, [string]$path) {
    $enc = ($path -split "/" | ForEach-Object { [Uri]::EscapeDataString($_) }) -join "/"
    return "https://raw.githubusercontent.com/$Repo/$sha/$enc"
}
function LocalPath([string]$path) { return Join-Path $Code ($path -replace "/", "\") }

Set-Content -Path $Log -Value "RAFFAELLO UPDATE $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  branch $Branch"
Say "RAFFAELLO UPDATE" "Yellow"
if (Test-Path (Join-Path $Code ".git")) { Fail "$Code is a git clone - use 'git pull' there (the patch updater is for the code\ folder)." }

# ---------------------------------------------------------------- 1. what is installed, what is latest
try { $latest = Invoke-RestMethod -Uri "$Api/commits/$Branch" -Headers $Headers } catch { Fail "cannot reach GitHub ($($_.Exception.Message)). Check the internet connection." }
$sha = $latest.sha
$base = if (Test-Path $State) { (Get-Content $State -Raw).Trim() } else { "" }
Say ("Installed: " + $(if ($base) { $base.Substring(0, 7) } else { "unknown (first patch update)" }) + "   Latest: " + $sha.Substring(0, 7))
if ($CheckOnly) { if ($base -eq $sha) { Say "Up to date." "Green" } else { Say "Update available." "Yellow" }; exit 0 }

if ($WaitPid -gt 0) { try { Wait-Process -Id $WaitPid -Timeout 30 -ErrorAction SilentlyContinue } catch {} }
Get-Process Raffaello -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$needBuild = -not (Test-Path $Exe)
$cmp = $null
if (-not $Full -and $base -eq $sha) {
    Say "Already up to date." "Green"
}
elseif (-not $Full -and $base) {
    try { $cmp = Invoke-RestMethod -Uri "$Api/compare/$base...$sha" -Headers $Headers } catch { $cmp = $null }
    if ($null -eq $cmp -or $cmp.status -ne "ahead" -or @($cmp.files).Count -ge 300) {
        Say "Cannot build a patch from the installed version (history changed or too many files) - full download instead." "Yellow"
        $cmp = $null; $Full = $true
    }
}
else { $Full = $true }

# ---------------------------------------------------------------- 2a. patch: only the changed files
if ($cmp -and -not $Full) {
    $files = @($cmp.files)
    Say ""
    Say "WHAT CHANGED ($($cmp.ahead_by) update(s), $($files.Count) file(s)):" "Cyan"
    foreach ($c in @($cmp.commits)) { Say ("  * " + ($c.commit.message -split "`n")[0]) "Cyan" }
    Say ""
    # download into a staging folder first, so a broken connection never leaves half a patch
    $stage = Join-Path $env:TEMP ("raffaello_patch_" + $sha.Substring(0, 7))
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    $i = 0
    foreach ($f in $files) {
        $i++
        if ($f.status -eq "removed") { continue }
        try { Invoke-WebRequest -Uri (RawUrl $sha $f.filename) -OutFile (Join-Path $stage "f$i") -UseBasicParsing }
        catch { Fail "download of $($f.filename) failed ($($_.Exception.Message))." }
    }
    $i = 0
    foreach ($f in $files) {
        $i++
        $dst = LocalPath $f.filename
        if ($f.status -eq "renamed" -and $f.previous_filename) {
            $old = LocalPath $f.previous_filename
            if (Test-Path $old) { Remove-Item $old -Force }
        }
        if ($f.status -eq "removed") {
            if (Test-Path $dst) { Remove-Item $dst -Force }
            Say "  - $($f.filename)"
        } else {
            New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
            Move-Item -Path (Join-Path $stage "f$i") -Destination $dst -Force
            Say "  $(if ($f.status -eq 'added') { '+' } else { '~' }) $($f.filename)"
        }
        if ($f.filename -notmatch '^(docs/|design-system/)' -and $f.filename -notmatch '\.md$') { $needBuild = $true }
    }
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Set-Content -Path $State -Value $sha
    Say ""; Say "Patch applied." "Green"
}

# ---------------------------------------------------------------- 2b. full download (first time only)
if ($Full) {
    Say "Downloading the full code once (later updates are patches)..."
    $zip = Join-Path $env:TEMP "raffaello_full.zip"; $tmpDir = Join-Path $env:TEMP "raffaello_full"
    if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
    try { Invoke-WebRequest -Uri "https://github.com/$Repo/archive/$sha.zip" -OutFile $zip -UseBasicParsing } catch { Fail "download failed ($($_.Exception.Message))." }
    Expand-Archive -Path $zip -DestinationPath $tmpDir -Force
    $src = (Get-ChildItem $tmpDir -Directory | Select-Object -First 1).FullName
    # keep bin / obj so the build stays incremental
    robocopy $src $Code /MIR /XD bin obj publish server-publish out .git /XF .raffaello_commit build_log.txt diag_log.txt /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "copying the files failed (robocopy $LASTEXITCODE)." }
    Remove-Item $zip -Force -ErrorAction SilentlyContinue; Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    Set-Content -Path $State -Value $sha
    $needBuild = $true
    Say "Full code installed." "Green"
}

# ---------------------------------------------------------------- 3. quick build
if ($needBuild) {
    Say "Building (quick, no tests)..."
    $out = & dotnet build (Join-Path $Code "src\Raffaello.App\Raffaello.App.csproj") -c Debug -nologo -v q 2>&1
    $out | Add-Content -Path $Log
    if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 15 | ForEach-Object { Write-Host $_ -ForegroundColor Red }; Fail "build error (details above)." }
    Say "Build OK." "Green"
} else { Say "No code changes - no build needed." }

# keep the launcher next to code\ current (not while the launcher itself is running)
$launcher = Join-Path $Root "UPDATE_AND_RUN.bat"; $newLauncher = Join-Path $Code "UPDATE_AND_RUN.bat"
if (-not $env:RAFFAELLO_FROM_BAT -and (Test-Path $newLauncher) -and (Test-Path $launcher)) {
    if ((Get-FileHash $launcher).Hash -ne (Get-FileHash $newLauncher).Hash) { Copy-Item $newLauncher $launcher -Force; Say "UPDATE_AND_RUN.bat refreshed." }
}

# ---------------------------------------------------------------- 4. start
Add-Content -Path $Log -Value "UPDATE OK"
if (-not $NoStart) { Say "Starting Raffaello..." "Yellow"; Start-Process $Exe }
Start-Sleep -Seconds 2
exit 0
