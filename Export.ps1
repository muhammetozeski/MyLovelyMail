# Publishes the Windows app into a NEW versioned folder under the deployment root and builds the
# launcher shim next to it. A fresh folder per export avoids file locks from a running instance.
# Layout produced:  <DeployRoot>\v###\MyLovelyMail.exe (launcher)  +  <DeployRoot>\v###\AppData\* (app)
# UserData/UserCache/AppCache are created by the app itself on first run — never here.

param(
    [string]$DeployRoot = "C:\E\kp\aaBenimProgramlarim\MyLovelyMail",
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = 'Stop'
$SlnDir = $PSScriptRoot
$MauiCsproj = Join-Path $SlnDir "MyLovelyMail\MyLovelyMail.csproj"
$Tfm = "net10.0-windows10.0.19041.0"

# ── Pick the next free version folder ──
New-Item -ItemType Directory -Force $DeployRoot | Out-Null
$lastVersion = Get-ChildItem $DeployRoot -Directory -Filter 'v*' |
    ForEach-Object { [int]($_.Name -replace '\D', '0') } |
    Sort-Object | Select-Object -Last 1
$versionNumber = if ($null -eq $lastVersion) { 1 } else { $lastVersion + 1 }
$VersionDir = Join-Path $DeployRoot ("v{0:D3}" -f $versionNumber)
$AppDir = Join-Path $VersionDir "AppData"
New-Item -ItemType Directory -Force $AppDir | Out-Null

# ── Publish the MAUI Windows app into AppData ──
Write-Host "Publishing $Tfm / $Rid into $AppDir ..." -ForegroundColor Cyan
dotnet publish $MauiCsproj -f $Tfm -r $Rid -c Release -o $AppDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$TargetExe = Join-Path $AppDir "MyLovelyMail.exe"
if (-not (Test-Path $TargetExe)) { throw "Published exe not found: $TargetExe" }

# ── Build the launcher shim (starts AppData\MyLovelyMail.exe forwarding all arguments) ──
$WorkDir = Join-Path $env:TEMP "MyLovelyMailLauncher_$(New-Guid)"
New-Item -ItemType Directory -Force $WorkDir | Out-Null

# Reuse the app's own icon for the shim.
Add-Type -AssemblyName System.Drawing
$IconXml = ""
try {
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($TargetExe)
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

string appDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppData");
string targetExe = Path.Combine(appDirectory, "MyLovelyMail.exe");

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

[DllImport("user32.dll", CharSet = CharSet.Auto)]
static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
'@

Write-Host "Building launcher shim..." -ForegroundColor Cyan
dotnet publish "$WorkDir\Launcher.csproj" -c Release -r $Rid -o "$WorkDir\Publish" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed." }

Copy-Item "$WorkDir\Publish\MyLovelyMail.exe" (Join-Path $VersionDir "MyLovelyMail.exe") -Force
Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue

Write-Host "Export ready: $VersionDir" -ForegroundColor Green
$VersionDir
