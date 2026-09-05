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
# DRAFTS ARE EXCLUDED, and that is not a detail. A draft has no git tag, so picking one as the
# last release sends the range below at a ref that exists nowhere, git fails with "ambiguous
# argument", $subjects comes back empty and the script dies claiming there is nothing to release
# while a dozen commits wait. A draft is also exactly what an interrupted run leaves behind: two
# are sitting in this repository right now, both with zero assets, one from a run that lost its
# network mid-upload and could not even delete its own.
$tags = @(gh release list --limit 100 --json tagName,isDraft --jq '.[] | select(.isDraft | not) | .tagName')
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
# Say which of the two things went wrong. "Nothing to release" used to be printed for both, and
# it is the wrong sentence for a missing tag: it points at the commits instead of at the ref.
git rev-parse --verify --quiet "$lastTag^{commit}" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "The newest published release is $lastTag, but no such tag exists here or on the remote even after fetching, so the notes cannot be built. Check whether that release was published without its tag."
}
$subjects = git log "$lastTag..HEAD" --no-merges --pretty=format:'- %s'
if (-not $subjects) { throw "No commits since $lastTag - nothing to release." }

# A draft already holding this number is not a collision - it is this script's own way of being
# resumable, so it is reported and reused rather than refused.
$resumingDraft = @(gh release list --limit 100 --json tagName,isDraft --jq '.[] | select(.isDraft) | .tagName' 2>$null) |
    Where-Object { $_ -eq $tag }
if ($resumingDraft) { Write-Host "A draft for $tag is already open; its assets will be completed and it will be published." -ForegroundColor Yellow }
$notesFile = Join-Path $env:TEMP "$ProjectName-$tag-notes.md"
Set-Content -Path $notesFile -Value "## What changed`n`n$($subjects -join "`n")" -Encoding UTF8

if ($WhatIf) { Write-Host "WhatIf: would stamp $($tag.TrimStart('v')) and release $tag with $(($subjects -split "`n").Count) entries."; return }

# ── Stamp the version into the single source, and land it BEFORE the tag ──
# AppConstants.AppVersion reads the running assembly, whose number comes from the <Version> element
# in Directory.Build.props. The tag below targets HEAD, so the stamp has to be committed first: cut
# the release from an unstamped commit and the tag names one version while the screen prints another,
# which is the disagreement this stamping exists to make impossible.
$propsFile = Join-Path $SlnDir 'Directory.Build.props'
$number = $tag.TrimStart('v')
$propsText = Get-Content $propsFile -Raw
if ($propsText -notmatch '<Version>[^<]*</Version>') { throw "Directory.Build.props has no <Version> element to stamp." }
$stamped = [regex]::Replace($propsText, '<Version>[^<]*</Version>', "<Version>$number</Version>")
if ($stamped -ne $propsText) {
    Set-Content -Path $propsFile -Value $stamped -NoNewline -Encoding utf8
    git add -- $propsFile
    git commit -m "Version follows the $tag release"
    if ($LASTEXITCODE -ne 0) { throw "Could not commit the version stamp." }
    git push
    if ($LASTEXITCODE -ne 0) { throw "Could not push the version stamp; the tag would point at an unstamped commit." }
    Write-Host "Stamped $number into Directory.Build.props." -ForegroundColor DarkGray
}

# ── Snapshot of what is being built ──
# The two flavours are two separate publishes minutes apart, and nothing checked that they saw the
# same source. They did not, once: an edit landed between them, so the portable zip held the tagged
# code and the framework-dependent zip held that plus an uncommitted change - one release, two
# different programs, and no way to tell from the outside. The state is recorded here and checked
# again before the release is cut.
$sourceStateBefore = @(git status --porcelain) -join "`n"
$headBefore = git rev-parse HEAD

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

    & (Join-Path $SlnDir "BuildLauncher.ps1") -Root $stageRoot -IconSourceExe "$appData\$ProjectName.exe" -IconFile (Join-Path $SlnDir "MyLovelyMail\Resources\Raw\trayicon.ico") -Rid $Rid
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
if ($head -ne $headBefore -or (@(git status --porcelain) -join "`n") -ne $sourceStateBefore) {
    throw "The working tree changed while the assets were being built, so they were not all made from the same source. Nothing was published; re-run the release on a settled tree."
}
# Draft first, assets one at a time, publish last. `gh release create` with the assets attached is
# all-or-nothing, and on a flaky link that is the wrong shape: three runs in a row built both zips -
# eight minutes each - and then died on the upload, twice on DNS for uploads.github.com and once on
# the local network aborting the 133 MB POST. Each failure threw away the whole build, and one left
# a draft gh could not delete because the same network was gone.
#
# This way a dropped upload costs one asset, a re-run resumes into the same draft, and a half-done
# attempt is never visible to anyone: nothing is published until every asset is confirmed present.
if (-not $resumingDraft) {
    gh release create $tag --draft --title $tag --notes-file $notesFile --target $head
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
}

# Smallest first, so a bad link proves itself on the cheap asset instead of the big one.
foreach ($asset in ($assets | Sort-Object { (Get-Item $_).Length })) {
    $name = Split-Path $asset -Leaf
    $uploaded = $false
    foreach ($attempt in 1..5) {
        Write-Host "Uploading $name (attempt $attempt/5)..." -ForegroundColor DarkGray
        gh release upload $tag $asset --clobber
        if ($LASTEXITCODE -eq 0) { $uploaded = $true; break }
        Start-Sleep -Seconds (10 * $attempt)
    }
    if (-not $uploaded) {
        throw "Could not upload $name after 5 attempts. The draft for $tag is kept with whatever did upload - re-run this script to resume."
    }
}

$present = @(gh release view $tag --json assets --jq '.assets[].name')
if ($present.Count -ne $assets.Count) {
    throw "$tag has $($present.Count) of $($assets.Count) assets, so it was left as a draft. Re-run this script to finish it."
}

gh release edit $tag --draft=false
if ($LASTEXITCODE -ne 0) { throw "The assets are all uploaded but $tag could not be published; re-run to finish it." }
Remove-Item $notesFile -ErrorAction SilentlyContinue
Write-Host "Released $tag" -ForegroundColor Green
$tag
