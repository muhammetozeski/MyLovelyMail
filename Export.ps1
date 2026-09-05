# Publishes the Windows app into the STABLE deploy root so user data survives updates.
# Layout produced (and kept stable across versions):
#   <DeployRoot>\MyLovelyMail.exe    launcher (applies pending updates, migrates data, starts the app)
#   <DeployRoot>\AppData\            the running application (replaced in place when not locked)
#   <DeployRoot>\PendingUpdate\      new AppData staged here when the app is running; launcher applies it next start
#   <DeployRoot>\Versions\vNNN\      archive copy of each exported build (for inspection/rollback)
#   <DeployRoot>\UserData|UserCache|AppCache  created by the app itself, NEVER touched here
param(
    [string]$DeployRoot = "C:\E\kp\aaBenimProgramlarim\MyLovelyMail",
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = 'Stop'
$SlnDir = $PSScriptRoot
$MauiCsproj = Join-Path $SlnDir "MyLovelyMail\MyLovelyMail.csproj"
$Tfm = "net10.0-windows10.0.19041.0"

New-Item -ItemType Directory -Force $DeployRoot | Out-Null

# Is the installed copy running? The app holds AppCache\run-lock.txt open for writing for as long
# as it is up and shares reads only, so a refused write IS the answer. Windows drops the handle
# when the process dies - a killed app leaves no stale lock - so this cannot answer "yes" for a
# copy that is already gone. When the file is missing (fresh install, cleared cache) the process
# list still gets a say, because guessing "not running" is the guess that deletes a live install.
function Test-DeployedAppRunning {
    param([string]$Root)

    $lockFile = Join-Path $Root "AppCache\run-lock.txt"
    if (Test-Path $lockFile) {
        try {
            $probe = [IO.File]::Open($lockFile, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $probe.Close()
        } catch {
            return $true
        }
    }
    return [bool](Get-Process -Name 'MyLovelyMail' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase) })
}

# ── Pick the next archive version number ──
$versionsDir = Join-Path $DeployRoot "Versions"
New-Item -ItemType Directory -Force $versionsDir | Out-Null
$lastVersion = @(Get-ChildItem $DeployRoot, $versionsDir -Directory -Filter 'v*' -ErrorAction SilentlyContinue |
    ForEach-Object { [int]($_.Name -replace '\D', '0') }) | Sort-Object | Select-Object -Last 1
$versionNumber = if ($null -eq $lastVersion) { 1 } else { $lastVersion + 1 }
$versionTag = "v{0:D3}" -f $versionNumber

# ── Publish into a staging folder ──
$staging = Join-Path $DeployRoot "PendingUpdate\AppData"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
Write-Host "Publishing $Tfm / $Rid ($versionTag) into staging..." -ForegroundColor Cyan
# --self-contained is explicit: since SDK 6, -r alone no longer implies it, and without it the
# deploy silently becomes framework-dependent (breaks on machines without the .NET runtime).
dotnet publish $MauiCsproj -f $Tfm -r $Rid -c Release --self-contained true -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
if (-not (Test-Path "$staging\MyLovelyMail.exe")) { throw "Published exe not found in staging." }

# ── Archive copy for inspection/rollback ──
# Every archive is a full self-contained publish - about 335 MB - and nothing ever removed one,
# so the folder grew by that much per export until the disk said no: this line is where an export
# died with "there is not enough space on the disk", leaving a half-copied v004 behind. Keeping
# the newest few is what the folder is actually for; the rest were only taking up room.
$keptVersions = 3
$stale = @(Get-ChildItem $versionsDir -Directory -Filter 'v*' -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.Name -replace '\D', '0') } | Select-Object -SkipLast ($keptVersions - 1))
foreach ($old in $stale) {
    Remove-Item -Recurse -Force $old.FullName -ErrorAction SilentlyContinue
    Write-Host "  pruned Versions\$($old.Name)" -ForegroundColor DarkGray
}

$archive = Join-Path $versionsDir "$versionTag\AppData"
try {
    Copy-Item $staging $archive -Recurse -ErrorAction Stop
    Write-Host "Archived as Versions\$versionTag" -ForegroundColor DarkGray
} catch {
    # A partial archive is worse than none: it looks like a build that can be rolled back to, and
    # it takes the same disk space while being unable to run.
    Remove-Item -Recurse -Force (Join-Path $versionsDir $versionTag) -ErrorAction SilentlyContinue
    throw "Archiving $versionTag failed, so the partial copy was removed: $($_.Exception.Message)"
}

# ── Apply the update NOW when the app is closed; otherwise leave it staged for the launcher ──
# This used to delete AppData first and discover the app was running only when it reached the
# locked executable - by then it had already deleted everything sorting before it (73 localization
# folders, one time). So: ask first, and swap by RENAME, which either takes the whole folder or
# takes nothing.
$liveAppData = Join-Path $DeployRoot "AppData"
$retired = Join-Path $DeployRoot "AppData.retired"

if (Test-DeployedAppRunning $DeployRoot) {
    Write-Host "MyLovelyMail is running - nothing touched; the update stays staged in PendingUpdate and the launcher applies it on next start." -ForegroundColor Yellow
} else {
    Remove-Item -Recurse -Force $retired -ErrorAction SilentlyContinue
    try {
        if (Test-Path $liveAppData) { Rename-Item $liveAppData "AppData.retired" -ErrorAction Stop }
        Move-Item $staging $liveAppData
        Remove-Item -Recurse -Force (Join-Path $DeployRoot "PendingUpdate") -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $retired -ErrorAction SilentlyContinue
        Write-Host "Live AppData updated in place." -ForegroundColor Green
    } catch {
        # A half-applied update is worse than an old one: put the previous install back.
        if ((Test-Path $retired) -and -not (Test-Path $liveAppData)) { Rename-Item $retired "AppData" }
        Write-Host "Could not swap AppData ($($_.Exception.Message)) - the update stays staged in PendingUpdate." -ForegroundColor Yellow
    }
}

# ── Build the launcher (applies pending updates, migrates old vNNN user data once, starts the app) ──
$iconSourceExe = if (Test-Path "$liveAppData\MyLovelyMail.exe") { "$liveAppData\MyLovelyMail.exe" } else { Join-Path $versionsDir "$versionTag\AppData\MyLovelyMail.exe" }
& (Join-Path $SlnDir "BuildLauncher.ps1") -Root $DeployRoot -IconSourceExe $iconSourceExe -Rid $Rid

Write-Host "Export ready: $DeployRoot ($versionTag)" -ForegroundColor Green
$DeployRoot