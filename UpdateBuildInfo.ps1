param(
    [switch]$Freeze
)

$ErrorActionPreference = 'Stop'
$RepoRoot = $PSScriptRoot
$BuildInfoPath = Join-Path $RepoRoot 'MainProject\Constants\BuildInfo.cs'
$AgentsPath = Join-Path $RepoRoot 'AGENTS.md'
$ObjDir = Join-Path $RepoRoot 'MainProject\obj'
$StampFile = Join-Path $ObjDir 'build-session.stamp'

if ($Freeze) {
    return
}

$mutex = New-Object System.Threading.Mutex($false, 'Global\MyLovelyMailBuildInfoMutex')
$acquired = $false
try {
    $acquired = $mutex.WaitOne(10000)

    $nowUtc = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    if (Test-Path -LiteralPath $StampFile) {
        $lastText = (Get-Content -LiteralPath $StampFile -Raw).Trim()
        $lastMs = 0L
        if ([long]::TryParse($lastText, [ref]$lastMs)) {
            if (($nowUtc - $lastMs) -ge 0 -and ($nowUtc - $lastMs) -lt 3000) {
                return
            }
        }
    }

    if (-not (Test-Path -LiteralPath $ObjDir)) {
        New-Item -ItemType Directory -Path $ObjDir | Out-Null
    }
    Set-Content -LiteralPath $StampFile -Value $nowUtc.ToString() -NoNewline -Encoding UTF8

    $currentNumber = 49
    if (Test-Path -LiteralPath $BuildInfoPath) {
        $raw = Get-Content -LiteralPath $BuildInfoPath -Raw
        if ($raw -match 'BuildNumber\s*=\s*(\d+)') {
            $currentNumber = [int]$Matches[1]
        }
    }

    $nextNumber = $currentNumber + 1

    $commitHash = 'unknown'
    $gitOut = & git -C $RepoRoot rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitOut)) {
        $commitHash = $gitOut.Trim()
    }

    $newBuildInfo = @"
namespace MyLovelyMail.MainProject.Constants;

public static class BuildInfo
{
    public const int BuildNumber = $nextNumber;
    public const string CommitHash = "$commitHash";
}
"@
    Set-Content -LiteralPath $BuildInfoPath -Value $newBuildInfo -Encoding UTF8

    if (Test-Path -LiteralPath $AgentsPath) {
        $agentsRaw = Get-Content -LiteralPath $AgentsPath -Raw
        $headerBlock = @"
<!-- BUILD_INFO_START -->
- **Latest Build Number**: $nextNumber (Mandatory: increment by +1 on every build and include in every commit message)
- **Latest Commit ID (at last build)**: ``$commitHash``
<!-- BUILD_INFO_END -->
"@
        if ($agentsRaw -match '(?s)<!-- BUILD_INFO_START -->.*?<!-- BUILD_INFO_END -->') {
            $agentsUpdated = [regex]::Replace($agentsRaw, '(?s)<!-- BUILD_INFO_START -->.*?<!-- BUILD_INFO_END -->', $headerBlock)
        } else {
            $agentsUpdated = $headerBlock + "`r`n`r`n" + $agentsRaw
        }
        Set-Content -LiteralPath $AgentsPath -Value $agentsUpdated -NoNewline -Encoding UTF8
    }
}
finally {
    if ($acquired) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
