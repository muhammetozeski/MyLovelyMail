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
dotnet publish $MauiCsproj -f $Tfm -r $Rid -c Release -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
if (-not (Test-Path "$staging\MyLovelyMail.exe")) { throw "Published exe not found in staging." }

# ── Archive copy for inspection/rollback ──
Copy-Item $staging (Join-Path $versionsDir "$versionTag\AppData") -Recurse
Write-Host "Archived as Versions\$versionTag" -ForegroundColor DarkGray

# ── Try to apply the update NOW (works when the app is closed; otherwise the launcher applies it) ──
$liveAppData = Join-Path $DeployRoot "AppData"
try {
    if (Test-Path $liveAppData) { Remove-Item -Recurse -Force $liveAppData }
    Move-Item $staging $liveAppData
    Remove-Item -Recurse -Force (Join-Path $DeployRoot "PendingUpdate") -ErrorAction SilentlyContinue
    Write-Host "Live AppData updated in place." -ForegroundColor Green
} catch {
    Write-Host "App seems to be running - update staged in PendingUpdate; the launcher applies it on next start." -ForegroundColor Yellow
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

// Apply a staged update. Move fails while the old build is still running - then just start it;
// the update is applied on the next launch instead.
if (Directory.Exists(pendingAppData))
{
    try
    {
        if (Directory.Exists(appDirectory))
            Directory.Delete(appDirectory, recursive: true);
        Directory.Move(pendingAppData, appDirectory);
        Directory.Delete(Path.Combine(root, "PendingUpdate"), recursive: true);
    }
    catch { /* locked by a running instance - keep the current build */ }
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