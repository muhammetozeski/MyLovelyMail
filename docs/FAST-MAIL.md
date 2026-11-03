# Fast Mail creation, credentials and recovery

The lightning/green-plus button beside an account folder's add button creates Mail.tm directly
in that folder. `FastMailAccountService` owns provisioning, vault storage and Inbox creation for
both this shortcut and the wizard. Repeated clicks in the same folder are disabled while pending.
Provider requests use Tor. Mail.tm's password is shown only after pressing **Show password** in
that account's settings, with the vault unlocked.

Generated aliases contain 5–10 lowercase ASCII letters, alternating vowels and consonants. Length,
initial category and every character use `RandomNumberGenerator.GetInt32`. No seeded ordinary
pseudorandom generator is involved.

`PasswordGenerator.cs` and `PasswordOptions.cs` were copied from the existing password generator
and adapted into the Fast Mail namespace. Its character selection and shuffle remain cryptographic;
the combined pool is deduplicated. Defaults include upper/lowercase, digits, standard and extended
symbols and spaces. The wizard exposes category, ambiguous-character and space choices. Generation
targets 256 bits with at least 40 characters, extends length for smaller pools and rejects candidates
below the existing generator's 230-bit estimate. Attempt 3000 is logged and throws without returning
a weak candidate. The displayed estimate is the copied generator's estimate, not a mathematical
proof of the entropy of an arbitrary human password.

## Local deletion and recovery

Only Fast Mail has the sidebar's **Delete Fast Mail account** action. Two independent confirmations
are required. The oldest normal, non-Tor account with an unlocked stored password supplies the
recovery password; the second prompt names it. The key starts with that password's UTF-8 bytes
and applies SHA-256 to the previous digest 1500 times, producing a 32-byte AES-GCM key.

`FastMailAccountOperations` cancels active work and obtains the account's exclusive gate before
snapshotting. The encrypted ZIP contains the account definition/settings, cached and local mail,
its vault password and referenced local draft attachments. Authenticated encryption is checked
before writing the standalone recovery script. Windows receives that encrypted script in its
Recycle Bin before plaintext account files and the live vault entry are removed. Server mailboxes
are not deleted. No normal-account secret is embedded in the recovery file.

Restore the `RestoreFastMail-*.ps1` item from the Recycle Bin, close My Lovely Mail completely, and
run it with PowerShell 7 (`pwsh -File <restored-script-path>`). It asks for the named normal account's
password as it was at deletion time and, for a master-password vault, its current master password.
It restores into the recorded application root by default and merges the recovered password into
the current vault. Unrelated secrets are retained; an existing different file is refused. Recovery
files must be kept if the normal account's password is later changed: decrypting requires its old
password. Missing normal-account credentials stop deletion before any account data is removed.

`tests/AnonymityChecks/VerifyRecovery.ps1` executed generated scripts against synthetic stores:
wrong passwords and modified ciphertext were refused before writes; file bytes were restored;
master-vault merging retained unrelated secrets; repeated restore and a fresh Windows device-bound
vault succeeded. The UI's two confirmations and Windows Recycle Bin interaction require native
interaction; the automated restore checks exercise the generated script and vault formats.
