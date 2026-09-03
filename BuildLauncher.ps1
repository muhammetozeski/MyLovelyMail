# Builds the MyLovelyMail launcher shim (applies a staged AppData update, migrates old vNNN
# UserData once, then starts AppData\MyLovelyMail.exe) and writes it to <Root>\MyLovelyMail.exe.
# Shared by Export.ps1 (live deploy root) and Release.ps1 (GitHub release package roots) so the
# shim's logic has one source of truth instead of two copies drifting apart.
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$IconSourceExe,
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = 'Stop'

$WorkDir = Join-Path $env:TEMP "MyLovelyMailLauncher_$(New-Guid)"
New-Item -ItemType Directory -Force $WorkDir | Out-Null

Add-Type -AssemblyName System.Drawing
$IconXml = ""
try {
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($IconSourceExe)
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
    Copy-Item "$WorkDir\Publish\MyLovelyMail.exe" (Join-Path $Root "MyLovelyMail.exe") -Force
    Write-Host "Launcher written to $Root" -ForegroundColor Green
} catch {
    Write-Host "Launcher exe is locked at $Root - kept the existing one." -ForegroundColor Yellow
}
Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
