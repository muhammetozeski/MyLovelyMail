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

# ── Next version from the HIGHEST existing release tag ──
# Not "the newest": GitHub reports a release's createdAt from its tag, which can predate an
# earlier release, so listing by date once handed back v1.2.0 while v1.3.0 already existed.
$tags = @(gh release list --limit 100 --json tagName --jq '.[].tagName')
$lastTag = if ($tags) {
    ($tags | Sort-Object { [version]($_.TrimStart('v')) } | Select-Object -Last 1)
} else { 'v0.0.0' }
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
# gh creates the tag on the remote, so the local repository has never heard of it and the range
# below silently resolves to nothing. Fetch first or the notes come out empty.
git fetch --tags --quiet 2>&1 | Out-Null
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
    # Package root layout mirrors Export.ps1's live deploy root, so a GitHub download and a local
    # export behave the same way: <root>\MyLovelyMail.exe (launcher) + <root>\AppData\ (the app).
    $stageRoot = Join-Path $PublishDir $stage.Name
    $appData = Join-Path $stageRoot 'AppData'
    if (Test-Path $stageRoot) { Remove-Item -Recurse -Force $stageRoot }
    Write-Host "Publishing $($stage.Name) (self-contained=$($stage.SelfContained))..." -ForegroundColor Cyan
    dotnet publish $MauiCsproj -f $Tfm -r $Rid -c Release --self-contained $stage.SelfContained -o $appData
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($stage.Name)." }
    if (-not (Test-Path "$appData\$ProjectName.exe")) { throw "Published exe missing in $appData." }

    & (Join-Path $SlnDir "BuildLauncher.ps1") -Root $stageRoot -IconSourceExe "$appData\$ProjectName.exe" -Rid $Rid
    if (-not (Test-Path "$stageRoot\$ProjectName.exe")) { throw "Launcher exe missing in $stageRoot." }

    $zip = Join-Path $PublishDir $stage.Asset
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path "$stageRoot\*" -DestinationPath $zip
    $assets += $zip
    Write-Host "  -> $($stage.Asset) ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor DarkGray
}

# ── Publish the release ──
# --target is mandatory: without it gh tags the repository's DEFAULT branch, which on this
# project trails the working branch by a hundred commits - the assets would be built from code
# the tag does not point at.
$head = git rev-parse HEAD
gh release create $tag $assets --title $tag --notes-file $notesFile --target $head
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
Remove-Item $notesFile -ErrorAction SilentlyContinue
Write-Host "Released $tag" -ForegroundColor Green
$tag
