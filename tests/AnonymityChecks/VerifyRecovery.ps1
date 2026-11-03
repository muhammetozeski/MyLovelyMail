param([Parameter(Mandatory)][string]$FixturePath, [Parameter(Mandatory)][string]$TestRoot)
$ErrorActionPreference = 'Stop'
$fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
if (!$fixture.DataRoot.StartsWith($TestRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'The fixture must be inside its NewTemp root.' }
$scratch = Join-Path $TestRoot ('RecoveryRestore' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
function Assert-Check([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw "FAIL: $Name" }
    Write-Host "PASS: $Name"
}
function Assert-Syntax([string]$Path) {
    $tokens = $null; $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw ($errors.Message -join '; ') }
}
function Open-TestVault([string]$Root, [string]$Master) {
    $vault = Get-Content -LiteralPath (Join-Path $Root 'UserData/vault.json') -Raw | ConvertFrom-Json
    $blob = [Convert]::FromBase64String($vault.Payload)
    if ([int]$vault.Mode -eq 0) {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect($blob, [IO.File]::ReadAllBytes((Join-Path $Root 'UserData/vault.entropy')), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    } else {
        $key = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2([Text.Encoding]::UTF8.GetBytes($Master), [Convert]::FromBase64String($vault.Salt), 200000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
        $bytes = [byte[]]::new($blob.Length - 28)
        $aes = [Security.Cryptography.AesGcm]::new($key, 16)
        try { $aes.Decrypt([byte[]]$blob[0..11], [byte[]]$blob[28..($blob.Length - 1)], [byte[]]$blob[12..27], $bytes) }
        finally { $aes.Dispose() }
    }
    return ([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable)
}
Assert-Syntax $fixture.ScriptPath
$recoveryPassword = ConvertTo-SecureString $fixture.NormalPassword -AsPlainText -Force
$masterPassword = ConvertTo-SecureString $fixture.MasterPassword -AsPlainText -Force
$wrongRoot = Join-Path $scratch 'WrongPassword'
$rejected = $false
try { & $fixture.ScriptPath -DataRoot $wrongRoot -RecoveryPassword (ConvertTo-SecureString 'wrong' -AsPlainText -Force) -NoPause }
catch { $rejected = $true }
Assert-Check ($rejected -and !(Test-Path -LiteralPath (Join-Path $wrongRoot 'UserData'))) 'Wrong recovery password is rejected before writing data'

$restored = Join-Path $scratch 'MasterVault'
[IO.Directory]::CreateDirectory((Join-Path $restored 'UserData')) | Out-Null
Copy-Item -LiteralPath (Join-Path $fixture.DataRoot 'UserData/vault.json') -Destination (Join-Path $restored 'UserData/vault.json')
& $fixture.ScriptPath -DataRoot ($restored + '\') -RecoveryPassword $recoveryPassword -VaultPassword $masterPassword -NoPause
$secrets = Open-TestVault $restored $fixture.MasterPassword
Assert-Check ($secrets[$fixture.AccountId] -ceq $fixture.FastPassword) 'Generated script restores the deleted Fast Mail password'
Assert-Check ($secrets[$fixture.AnchorId] -ceq $fixture.NormalPassword) 'Restoring an account preserves unrelated vault passwords'
foreach ($file in $fixture.Files) {
    $target = Join-Path $restored ([IO.Path]::GetRelativePath($fixture.DataRoot, $file))
    Assert-Check ((Get-FileHash -LiteralPath $file).Hash -ceq (Get-FileHash -LiteralPath $target).Hash) ('Original file bytes restored: ' + [IO.Path]::GetRelativePath($fixture.DataRoot, $file))
}
& $fixture.ScriptPath -DataRoot $restored -RecoveryPassword $recoveryPassword -VaultPassword $masterPassword -NoPause
Assert-Check ((Open-TestVault $restored $fixture.MasterPassword)[$fixture.AccountId] -ceq $fixture.FastPassword) 'Repeating a restore keeps the same account and credentials'

$fresh = Join-Path $scratch 'DeviceVault'
& $fixture.ScriptPath -DataRoot $fresh -RecoveryPassword $recoveryPassword -NoPause
Assert-Check ((Open-TestVault $fresh '')[$fixture.AccountId] -ceq $fixture.FastPassword) 'A fresh Windows store receives a usable device-bound vault'

$script = [IO.File]::ReadAllText($fixture.ScriptPath)
$match = [regex]::Match($script, "FromBase64String\('(?<payload>[A-Za-z0-9+/=]{40,})'\)")
$corrupt = [Convert]::FromBase64String($match.Groups['payload'].Value)
$corrupt[$corrupt.Length - 1] = $corrupt[$corrupt.Length - 1] -bxor 1
$tamperedPath = Join-Path $scratch 'TamperedRestore.ps1'
[IO.File]::WriteAllText($tamperedPath, $script.Replace($match.Groups['payload'].Value, [Convert]::ToBase64String($corrupt)))
Assert-Syntax $tamperedPath
$tamperedRoot = Join-Path $scratch 'Tampered'
$rejected = $false
try { & $tamperedPath -DataRoot $tamperedRoot -RecoveryPassword $recoveryPassword -NoPause }
catch { $rejected = $true }
Assert-Check ($rejected -and !(Test-Path -LiteralPath (Join-Path $tamperedRoot 'UserData'))) 'Ciphertext tampering is rejected before writing data'
Write-Host 'Recovery script checks passed.'
