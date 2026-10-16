$ErrorActionPreference = "Stop"

$PublishDir = Join-Path $PSScriptRoot "Publish"
$SlnDir = $PSScriptRoot
$ProjectName = Split-Path $PSScriptRoot -Leaf
$MauiCsproj = "$SlnDir\$ProjectName\$ProjectName.csproj"
$WebCsproj = "$SlnDir\$ProjectName.Web\$ProjectName.Web.csproj"

# Create Publish directory (clean start)
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse
}
New-Item -ItemType Directory -Path $PublishDir | Out-Null

# ── Web ──
Write-Host "Publishing Web..." -ForegroundColor Cyan
dotnet publish $WebCsproj -c Release -p:SkipAndroidTargets=true -o "$PublishDir\Web"
if ($LASTEXITCODE -ne 0) { throw "Web publish failed." }

# ── Windows ──
$WindowsRids = @("win-x64", "win-x86", "win-arm64")
foreach ($rid in $WindowsRids) {
    Write-Host "Publishing Windows / $rid..." -ForegroundColor Cyan
    dotnet publish $MauiCsproj -f net10.0-windows10.0.19041.0 -r $rid -c Release -p:SkipAndroidTargets=true -o "$PublishDir\Windows\$rid"
    if ($LASTEXITCODE -ne 0) { throw "Windows publish failed for $rid." }
}

# ── Android ──
$AndroidRids = @("android-arm64")
foreach ($rid in $AndroidRids) {
    Write-Host "Publishing Android / $rid..." -ForegroundColor Cyan
    dotnet publish $MauiCsproj -f net10.0-android -r $rid -c Release -p:AndroidPackageFormat=apk -o "$PublishDir\Android\$rid"
    if ($LASTEXITCODE -ne 0) { throw "Android publish failed for $rid." }
}

Write-Host "Publish completed!" -ForegroundColor Green
