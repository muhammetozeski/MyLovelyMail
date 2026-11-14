# Publishes the standalone Launcher\Launcher.csproj project to <Root>\MyLovelyMail.exe.
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [string]$IconSourceExe = "",
    [string]$IconFile = "",
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = 'Stop'
$LauncherCsproj = Join-Path $PSScriptRoot "Launcher\Launcher.csproj"
$StageDir = Join-Path $env:TEMP ("MyLovelyMailLauncher_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

Write-Host "Building launcher from Launcher\Launcher.csproj..." -ForegroundColor Cyan
$iconArg = @()
if ($IconFile -and (Test-Path -LiteralPath $IconFile)) {
    $iconArg = @("-p:ApplicationIcon=$IconFile")
}

dotnet publish $LauncherCsproj -c Release -r $Rid --self-contained false @iconArg -o $StageDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed." }

try {
    Copy-Item -LiteralPath (Join-Path $StageDir "MyLovelyMail.exe") -Destination (Join-Path $Root "MyLovelyMail.exe") -Force
    Write-Host "Launcher written to $Root" -ForegroundColor Green
} catch {
    Write-Host "Launcher exe is locked at $Root - kept the existing one." -ForegroundColor Yellow
}
