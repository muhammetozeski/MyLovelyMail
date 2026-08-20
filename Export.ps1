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
Copy-Item $staging (Join-Path $versionsDir "$versionTag\AppData") -Recurse
Write-Host "Archived as Versions\$versionTag" -ForegroundColor DarkGray

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
$WorkDir = Join-Path $env:TEMP "MyLovelyMailLauncher_$(New-Guid)"
New-Item -ItemType Directory -Force $WorkDir | Out-Null

Add-Type -AssemblyName System.Drawing
$IconXml = ""
$iconSourceExe = if (Test-Path "$liveAppData\MyLovelyMail.exe") { "$liveAppData\MyLovelyMail.exe" } else { Join-Path $versionsDir "$versionTag\AppData\MyLovelyMail.exe" }
try {
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($iconSourceExe)
    if ($icon) {
        $iconStream = [System.IO.File]::OpenWrite("$WorkDir\appicon.ico")
        $icon.Save($iconStream)
        $iconStream.Close(); $icon.Dispose()
        $IconXml = "<ApplicationIcon>appicon.ico</ApplicationIcon>"
    }
} catch { Write-Host "Icon extraction skipped: $_" }

Set-Content -Path "$WorkDir\Launcher.csproj" -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <AssemblyName>MyLovelyMail</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>false</SelfContained>
    <Product>MyLovelyMail Launcher</Product>
    $IconXml
  </PropertyGroup>
</Project>
"@

Set-Content -Path "$WorkDir\Program.cs" -Value @'
using System.Diagnostics;
using System.Runtime.InteropServices;

string root = AppDomain.CurrentDomain.BaseDirectory;
string appDirectory = Path.Combine(root, "AppData");
string pendingAppData = Path.Combine(root, "PendingUpdate", "AppData");
string targetExe = Path.Combine(appDirectory, "MyLovelyMail.exe");

// Apply a staged update, but never while a copy is up: the app holds AppCache\run-lock.txt open
// for writing for as long as it runs, and swapping the folder under a live process breaks
// everything it has not loaded yet. The swap is a rename, so a refusal leaves the install whole
// instead of half-deleted.
if (Directory.Exists(pendingAppData) && !IsAppRunning(root))
{
    string retired = Path.Combine(root, "AppData.retired");
    try
    {
        if (Directory.Exists(retired))
            Directory.Delete(retired, recursive: true);
        if (Directory.Exists(appDirectory))
            Directory.Move(appDirectory, retired);
        Directory.Move(pendingAppData, appDirectory);
        Directory.Delete(Path.Combine(root, "PendingUpdate"), recursive: true);
        Directory.Delete(retired, recursive: true);
    }
    catch
    {
        // Roll back to whatever was installed; a half-applied update is worse than an old one.
        if (Directory.Exists(retired) && !Directory.Exists(appDirectory))
            Directory.Move(retired, appDirectory);
    }
}

// One-time migration from the old per-version layout: adopt the newest vNNN\UserData sibling.
string userData = Path.Combine(root, "UserData");
if (!Directory.Exists(userData))
{
    string? newestOldUserData = Directory.GetDirectories(root, "v*")
        .Select(versionDir => Path.Combine(versionDir, "UserData"))
        .Where(Directory.Exists)
        .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
    if (newestOldUserData != null)
        CopyDirectory(newestOldUserData, userData);
}

if (!File.Exists(targetExe))
{
    MessageBox(IntPtr.Zero, "Application not found:\n" + targetExe, "MyLovelyMail Launcher", 0x10);
    return 1;
}

var startInfo = new ProcessStartInfo
{
    FileName = targetExe,
    WorkingDirectory = appDirectory,
    UseShellExecute = false
};
foreach (string argument in args)
    startInfo.ArgumentList.Add(argument);

Process.Start(startInfo);
return 0;

// Mirrors Export.ps1: a refused write on the run lock means a copy is up. The handle dies with
// the process, so a killed app cannot leave a lock that blocks every future update.
static bool IsAppRunning(string root)
{
    string lockFile = Path.Combine(root, "AppCache", "run-lock.txt");
    if (!File.Exists(lockFile))
        return false;
    try
    {
        using var probe = File.Open(lockFile, FileMode.Open, FileAccess.Write, FileShare.None);
        return false;
    }
    catch (IOException)
    {
        return true;
    }
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

[DllImport("user32.dll", CharSet = CharSet.Auto)]
static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
'@

Write-Host "Building launcher..." -ForegroundColor Cyan
dotnet publish "$WorkDir\Launcher.csproj" -c Release -r $Rid -o "$WorkDir\Publish" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed." }

try {
    Copy-Item "$WorkDir\Publish\MyLovelyMail.exe" (Join-Path $DeployRoot "MyLovelyMail.exe") -Force
    Write-Host "Launcher updated." -ForegroundColor Green
} catch {
    Write-Host "Launcher exe is locked - kept the existing one." -ForegroundColor Yellow
}
Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue

Write-Host "Export ready: $DeployRoot ($versionTag)" -ForegroundColor Green
$DeployRoot