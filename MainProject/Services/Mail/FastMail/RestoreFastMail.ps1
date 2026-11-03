param(
    [string]$DataRoot = '__APP_ROOT__',
    [Security.SecureString]$RecoveryPassword,
    [Security.SecureString]$VaultPassword,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { & pwsh -NoProfile -File $PSCommandPath; return }

function Open-AesPayload([byte[]]$Blob, [byte[]]$Key) {
    if ($Blob.Length -lt 29) { throw 'Invalid encrypted data.' }
    $plain = [byte[]]::new($Blob.Length - 28)
    $aes = [Security.Cryptography.AesGcm]::new($Key, 16)
    try { $aes.Decrypt([byte[]]$Blob[0..11], [byte[]]$Blob[28..($Blob.Length - 1)], [byte[]]$Blob[12..27], $plain) }
    finally { $aes.Dispose() }
    return ,$plain
}
function Protect-AesPayload([byte[]]$Plain, [byte[]]$Key) {
    $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
    $tag = [byte[]]::new(16)
    $cipher = [byte[]]::new($Plain.Length)
    $aes = [Security.Cryptography.AesGcm]::new($Key, 16)
    try { $aes.Encrypt($nonce, $Plain, $cipher, $tag) }
    finally { $aes.Dispose() }
    return ,([byte[]]($nonce + $tag + $cipher))
}
function Password-Bytes([Security.SecureString]$Password) {
    return ,([Text.Encoding]::UTF8.GetBytes([Net.NetworkCredential]::new('', $Password).Password))
}

try {
    $DataRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($DataRoot))
    $rootPrefix = $DataRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $running = Get-Process -Name MyLovelyMail -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw 'Close My Lovely Mail completely before restoring this account.' }
    if (!$RecoveryPassword) { $RecoveryPassword = Read-Host 'Password of normal mail account __RECOVERY_ACCOUNT__ at deletion time' -AsSecureString }
    $key = Password-Bytes $RecoveryPassword
    for ($i = 0; $i -lt 1500; $i++) { $key = [Security.Cryptography.SHA256]::HashData($key) }
    $plain = Open-AesPayload ([Convert]::FromBase64String('__ENCRYPTED_PAYLOAD__')) $key
    $memory = [IO.MemoryStream]::new($plain, $false)
    $zip = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $metadataReader = [IO.StreamReader]::new($zip.GetEntry('metadata.json').Open())
        try { $metadata = $metadataReader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
        finally { $metadataReader.Dispose() }
        $accountId = [string]$metadata.AccountId
        $parsedId = [Guid]::Empty
        if (![Guid]::TryParse($accountId, [ref]$parsedId)) { throw 'Invalid recovery account identifier.' }
        $pendingFiles = @()
        foreach ($entry in $zip.Entries) {
            if (!$entry.FullName.StartsWith('files/', [StringComparison]::Ordinal)) { continue }
            $relative = $entry.FullName.Substring(6).Replace('/', [IO.Path]::DirectorySeparatorChar)
            $allowed = @("UserData/Accounts/$accountId/", "UserCache/Accounts/$accountId/", 'AppCache/ComposeAttachments/')
            $portableName = $entry.FullName.Substring(6)
            if (!($allowed | Where-Object { $portableName.StartsWith($_, [StringComparison]::Ordinal) })) { throw 'The archive contains a path outside this account.' }
            $target = [IO.Path]::GetFullPath((Join-Path $DataRoot $relative))
            if (!$target.StartsWith($DataRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive path.' }
            $entryBytes = [IO.MemoryStream]::new()
            $source = $entry.Open()
            try { $source.CopyTo($entryBytes); $bytes = $entryBytes.ToArray() }
            finally { $source.Dispose(); $entryBytes.Dispose() }
            if (Test-Path -LiteralPath $target) {
                if (!([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($target))) -ceq [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)))) { throw "An existing file differs from the backup: $target" }
                continue
            }
            $pendingFiles += @{ Path = $target; Bytes = $bytes }
        }
        $userData = Join-Path $DataRoot 'UserData'
        [IO.Directory]::CreateDirectory($userData) | Out-Null
        $vaultPath = Join-Path $userData 'vault.json'
        $entropyPath = Join-Path $userData 'vault.entropy'
        $vault = if (Test-Path -LiteralPath $vaultPath) { [IO.File]::ReadAllText($vaultPath) | ConvertFrom-Json -AsHashtable } else { @{ Mode = 0; Salt = ''; Payload = '' } }
        $vaultKey = $null
        if ([int]$vault.Mode -eq 1) {
            if (!$VaultPassword) { $VaultPassword = Read-Host 'Current vault master password' -AsSecureString }
            $vaultKey = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2((Password-Bytes $VaultPassword), [Convert]::FromBase64String($vault.Salt), 200000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
            $vaultPlain = Open-AesPayload ([Convert]::FromBase64String($vault.Payload)) $vaultKey
        } else {
            $entropy = if (Test-Path -LiteralPath $entropyPath) { [IO.File]::ReadAllBytes($entropyPath) } else { [Security.Cryptography.RandomNumberGenerator]::GetBytes(32) }
            if ($vault.Payload) {
                try { $vaultPlain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($vault.Payload), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser) }
                catch [Security.Cryptography.CryptographicException] { $vaultPlain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($vault.Payload), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser) }
            } else { $vaultPlain = [Text.Encoding]::UTF8.GetBytes('{}') }
        }
        $secrets = [Text.Encoding]::UTF8.GetString($vaultPlain) | ConvertFrom-Json -AsHashtable
        if ($metadata.Password) { $secrets[$accountId] = [string]$metadata.Password }
        $updatedPlain = [Text.Encoding]::UTF8.GetBytes(($secrets | ConvertTo-Json -Compress -Depth 10))
        if ([int]$vault.Mode -eq 1) { $vault.Payload = [Convert]::ToBase64String((Protect-AesPayload $updatedPlain $vaultKey)) }
        else {
            [IO.File]::WriteAllBytes($entropyPath, $entropy)
            $vault.Payload = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect($updatedPlain, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser))
        }
        foreach ($file in $pendingFiles) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file.Path)) | Out-Null
            [IO.File]::WriteAllBytes($file.Path, $file.Bytes)
        }
        $temporary = Join-Path $userData ('vault.restore-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        [IO.File]::WriteAllText($temporary, ($vault | ConvertTo-Json -Compress -Depth 10))
        [IO.File]::Move($temporary, $vaultPath, $true)
        Write-Host 'Fast Mail restored. Open My Lovely Mail to use it.'
    } finally { $zip.Dispose(); $memory.Dispose() }
} catch { Write-Error $_; throw }
finally {
    if ($key) { [Array]::Clear($key, 0, $key.Length) }
    if ($plain) { [Array]::Clear($plain, 0, $plain.Length) }
    if (!$NoPause) { Read-Host 'Press Enter to close' | Out-Null }
}
