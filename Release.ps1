# Cuts a GitHub release: publishes both flavours, zips them and uploads them as the release assets.
# Version comes from the newest existing release tag, bumped by -Bump (patch|minor|major).
# The release notes are the commit subjects since that tag, so they describe what actually changed.
param(
    [ValidateSet('patch', 'minor', 'major')][string]$Bump = 'minor',
    [string]$Rid = 'win-x64',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$SlnDir = $PSScriptRoot
$ProjectName = Split-Path $PSScriptRoot -Leaf
$MauiCsproj = Join-Path $SlnDir "$ProjectName\$ProjectName.csproj"
$Tfm = 'net10.0-windows10.0.19041.0'
$PublishDir = Join-Path $SlnDir 'publish'

# ── Next version from the newest release tag ──
$lastTag = (gh release list --limit 1 --json tagName --jq '.[0].tagName')
if (-not $lastTag) { $lastTag = 'v0.0.0' }
$parts = $lastTag.TrimStart('v').Split('.')
[int]$major = $parts[0]; [int]$minor = $parts[1]; [int]$patch = $parts[2]
switch ($Bump) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}
$tag = "v$major.$minor.$patch"
Write-Host "$lastTag -> $tag" -ForegroundColor Cyan

# ── Notes: the commit subjects since the last release ──
$subjects = git log "$lastTag..HEAD" --no-merges --pretty=format:'- %s'
if (-not $subjects) { throw "No commits since $lastTag - nothing to release." }
$notesFile = Join-Path $env:TEMP "$ProjectName-$tag-notes.md"
Set-Content -Path $notesFile -Value "## What changed`n`n$($subjects -join "`n")" -Encoding UTF8

if ($WhatIf) { Write-Host "WhatIf: would release $tag with $(($subjects -split "`n").Count) entries."; return }

# ── Publish both flavours ──
New-Item -ItemType Directory -Force $PublishDir | Out-Null
$stages = @(
    @{ Name = 'PortableStage'; SelfContained = 'true'; Asset = "$ProjectName-$tag-$Rid-portable.zip" }
    @{ Name = 'FrameworkDependentStage'; SelfContained = 'false'; Asset = "$ProjectName-$tag-$Rid-framework-dependent-requires-net10.zip" }
)
$assets = @()
foreach ($stage in $stages) {
    $out = Join-Path $PublishDir $stage.Name
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Write-Host "Publishing $($stage.Name) (self-contained=$($stage.SelfContained))..." -ForegroundColor Cyan
    dotnet publish $MauiCsproj -f $Tfm -r $Rid -c Release --self-contained $stage.SelfContained -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($stage.Name)." }
    if (-not (Test-Path "$out\$ProjectName.exe")) { throw "Published exe missing in $out." }

    $zip = Join-Path $PublishDir $stage.Asset
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path "$out\*" -DestinationPath $zip
    $assets += $zip
    Write-Host "  -> $($stage.Asset) ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor DarkGray
}

# ── Publish the release ──
gh release create $tag $assets --title $tag --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
Remove-Item $notesFile -ErrorAction SilentlyContinue
Write-Host "Released $tag" -ForegroundColor Green
$tag
