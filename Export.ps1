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
Add-Type -AssemblyName Microsoft.VisualBasic
$SlnDir = $PSScriptRoot
$MauiCsproj = Join-Path $SlnDir "MyLovelyMail\MyLovelyMail.csproj"
$Tfm = "net10.0-windows10.0.19041.0"

function Send-DirToRecycleBin {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($Path, 'OnlyErrorDialogs', 'SendToRecycleBin')
    }
}

New-Item -ItemType Directory -Force $DeployRoot | Out-Null

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
    $procs = @(Get-Process -Name 'MyLovelyMail' 2>$null |
        Where-Object { $_.Path -and $_.Path.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase) })
    return [bool]$procs
}

# ── Pick the next archive version number ──
$versionsDir = Join-Path $DeployRoot "Versions"
New-Item -ItemType Directory -Force $versionsDir | Out-Null
$existingDirs = @()
if (Test-Path -LiteralPath $DeployRoot) { $existingDirs += Get-ChildItem -LiteralPath $DeployRoot -Directory -Filter 'v*' }
if (Test-Path -LiteralPath $versionsDir) { $existingDirs += Get-ChildItem -LiteralPath $versionsDir -Directory -Filter 'v*' }
$lastVersion = @($existingDirs | ForEach-Object { [int]($_.Name -replace '\D', '0') }) | Sort-Object | Select-Object -Last 1
$versionNumber = if ($null -eq $lastVersion) { 1 } else { $lastVersion + 1 }
$versionTag = "v{0:D3}" -f $versionNumber

# ── Publish into a staging folder ──
$staging = Join-Path $DeployRoot "PendingUpdate\AppData"
Send-DirToRecycleBin $staging
Write-Host "Publishing $Tfm / $Rid ($versionTag) into staging..." -ForegroundColor Cyan
dotnet publish $MauiCsproj -f $Tfm -r $Rid -c Release --self-contained true -p:SkipAndroidTargets=true -p:FreezeBuildInfo=true -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
if (-not (Test-Path "$staging\MyLovelyMail.exe")) { throw "Published exe not found in staging." }

# ── Archive copy for inspection/rollback ──
$keptVersions = 2
$stale = @(Get-ChildItem -LiteralPath $versionsDir -Directory -Filter 'v*' |
    Sort-Object { [int]($_.Name -replace '\D', '0') } | Select-Object -SkipLast ($keptVersions - 1))
foreach ($old in $stale) {
    Send-DirToRecycleBin $old.FullName
    Write-Host "  pruned Versions\$($old.Name)" -ForegroundColor DarkGray
}

$archive = Join-Path $versionsDir "$versionTag\AppData"
try {
    Copy-Item $staging $archive -Recurse -ErrorAction Stop
    Write-Host "Archived as Versions\$versionTag" -ForegroundColor DarkGray
} catch {
    Send-DirToRecycleBin (Join-Path $versionsDir $versionTag)
    throw "Archiving $versionTag failed, so the partial copy was recycled: $($_.Exception.Message)"
}

# ── Apply the update NOW when the app is closed; otherwise leave it staged for the launcher ──
$liveAppData = Join-Path $DeployRoot "AppData"
$retired = Join-Path $DeployRoot "AppData.retired"

if (Test-DeployedAppRunning $DeployRoot) {
    Write-Host "MyLovelyMail is running - nothing touched; the update stays staged in PendingUpdate and the launcher applies it on next start." -ForegroundColor Yellow
} else {
    Send-DirToRecycleBin $retired
    try {
        if (Test-Path $liveAppData) { Rename-Item $liveAppData "AppData.retired" -ErrorAction Stop }
        Move-Item $staging $liveAppData
        Send-DirToRecycleBin (Join-Path $DeployRoot "PendingUpdate")
        Send-DirToRecycleBin $retired
        Write-Host "Live AppData updated in place." -ForegroundColor Green
    } catch {
        if ((Test-Path $retired) -and -not (Test-Path $liveAppData)) { Rename-Item $retired "AppData" }
        Write-Host "Could not swap AppData ($($_.Exception.Message)) - the update stays staged in PendingUpdate." -ForegroundColor Yellow
    }
}

# ── Build the launcher (applies pending updates, migrates old vNNN user data once, starts the app) ──
$iconSourceExe = if (Test-Path "$liveAppData\MyLovelyMail.exe") { "$liveAppData\MyLovelyMail.exe" } else { Join-Path $versionsDir "$versionTag\AppData\MyLovelyMail.exe" }
& (Join-Path $SlnDir "BuildLauncher.ps1") -Root $DeployRoot -IconSourceExe $iconSourceExe -IconFile (Join-Path $SlnDir "MyLovelyMail\Resources\Raw\trayicon.ico") -Rid $Rid

Write-Host "Export ready: $DeployRoot ($versionTag)" -ForegroundColor Green
$DeployRoot
