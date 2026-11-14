# Regenerates the KDBX reader fixtures with KeePass 2 itself (KeePassLib inside KeePass.exe), an
# implementation independent of the Atlas reader. Development tool only: KeePass is not a build, test or
# runtime dependency and nothing of it is copied into the repository. Requires Windows PowerShell 5.1
# (KeePass.exe targets .NET Framework) and a local KeePass 2.x installation.
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/fixtures/kdbx/generate-fixtures.ps1
#
# The script writes *.kdbx and manifest.json next to itself. The manifest is produced by reopening every
# saved file with KeePass, so expected values are what KeePass reads, not what the script intended.
# UUIDs and seeds are random; regenerate all files together and commit them with the manifest.
param(
    [string]$KeePass = 'C:\Program Files\KeePass Password Safe 2\KeePass.exe',
    [string]$Output = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom($KeePass)

$Password = 'Atlas fixture password'
$UnicodePassword = "Heslo $([char]0x017E)lu$([char]0x0165)ou$([char]0x010D)k$([char]0x00FD) k$([char]0x016F)$([char]0x0148) $([char]0x2713)"
$Now = [DateTime]::UtcNow
$Png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==')

function New-Key([string]$pw, [string]$keyFile) {
    $key = New-Object KeePassLib.Keys.CompositeKey
    $key.AddUserKey((New-Object KeePassLib.Keys.KcpPassword($pw)))
    if ($keyFile) { $key.AddUserKey((New-Object KeePassLib.Keys.KcpKeyFile($keyFile))) }
    return $key
}

function New-Database([string]$name, [string]$pw, [string]$kdf, [string]$cipher, [bool]$gzip, [string]$keyFile) {
    $db = New-Object KeePassLib.PwDatabase
    $db.New([KeePassLib.Serialization.IOConnectionInfo]::FromPath((Join-Path $Output $name)), (New-Key $pw $keyFile))
    $db.Name = [IO.Path]::GetFileNameWithoutExtension($name)
    $db.RootGroup.Name = 'Fixture root'
    switch ($kdf) {
        'aes' {
            $p = (New-Object KeePassLib.Cryptography.KeyDerivation.AesKdf).GetDefaultParameters()
            $p.SetUInt64('R', 2000)
        }
        default {
            $type = if ($kdf -eq 'argon2d') { [KeePassLib.Cryptography.KeyDerivation.Argon2Type]::D } else { [KeePassLib.Cryptography.KeyDerivation.Argon2Type]::ID }
            $p = (New-Object KeePassLib.Cryptography.KeyDerivation.Argon2Kdf($type)).GetDefaultParameters()
            # Small cost keeps the suite fast; the reader enforces limits, not a minimum.
            $p.SetUInt64('M', 1048576)
            $p.SetUInt64('I', 2)
            $p.SetUInt32('P', 2)
        }
    }
    $db.KdfParameters = $p
    if ($cipher -eq 'chacha20') { $db.DataCipherUuid = (New-Object KeePassLib.Cryptography.Cipher.ChaCha20Engine).CipherUuid }
    $db.Compression = if ($gzip) { [KeePassLib.PwCompressionAlgorithm]::GZip } else { [KeePassLib.PwCompressionAlgorithm]::None }
    return $db
}

function New-Group($parent, [string]$name) {
    $group = New-Object KeePassLib.PwGroup($true, $true, $name, [KeePassLib.PwIcon]::Folder)
    $parent.AddGroup($group, $true)
    return $group
}

function Set-Field($entry, [string]$key, [string]$value, [bool]$protected) {
    $entry.Strings.Set($key, (New-Object KeePassLib.Security.ProtectedString($protected, $value)))
}

function New-Entry($group, [string]$title, [string]$user, [string]$pw, [string]$url, [string]$notes, [bool]$protectNotes = $false) {
    $entry = New-Object KeePassLib.PwEntry($true, $true)
    Set-Field $entry 'Title' $title $false
    Set-Field $entry 'UserName' $user $false
    Set-Field $entry 'Password' $pw $true
    Set-Field $entry 'URL' $url $false
    Set-Field $entry 'Notes' $notes $protectNotes
    $group.AddEntry($entry, $true)
    return $entry
}

function Add-History($db, $entry, [string[]]$olderPasswords) {
    # Each backup keeps a protected password, so history subtrees carry inner-stream values.
    $current = $entry.Strings.ReadSafe('Password')
    foreach ($old in $olderPasswords) {
        Set-Field $entry 'Password' $old $true
        $entry.CreateBackup($db)
    }
    Set-Field $entry 'Password' $current $true
}

function Add-RecycleBin($db) {
    $bin = New-Object KeePassLib.PwGroup($true, $true, 'Recycle Bin', [KeePassLib.PwIcon]::TrashBin)
    $bin.EnableAutoType = $false
    $bin.EnableSearching = $false
    $db.RootGroup.AddGroup($bin, $true)
    $db.RecycleBinEnabled = $true
    $db.RecycleBinUuid = $bin.Uuid
    return $bin
}

function Save-Database($db, [uint32]$forceVersion = 0) {
    if ($forceVersion -eq 0) {
        $db.Save($null)
    } else {
        $file = New-Object KeePassLib.Serialization.KdbxFile($db)
        [KeePassLib.Serialization.KdbxFile].GetField('m_uForceVersion', [Reflection.BindingFlags]'NonPublic,Instance').SetValue($file, $forceVersion)
        $stream = [IO.File]::Create($db.IOConnectionInfo.Path)
        try { $file.Save($stream, $null, [KeePassLib.Serialization.KdbxFormat]::Default, $null) } finally { $stream.Close() }
    }
    $db.Close()
}

function Get-Path($group, $root) {
    $names = New-Object System.Collections.Generic.List[string]
    while ($group -ne $null -and -not $group.Uuid.Equals($root.Uuid)) { $names.Insert(0, $group.Name); $group = $group.ParentGroup }
    return ,$names.ToArray()
}

function Read-Manifest([string]$name, [string]$pw, [string]$keyFile, [string]$kdf, [string]$cipher, [bool]$gzip) {
    $path = Join-Path $Output $name
    $db = New-Object KeePassLib.PwDatabase
    $db.Open([KeePassLib.Serialization.IOConnectionInfo]::FromPath($path), (New-Key $pw $keyFile), $null)
    try {
        $bytes = [IO.File]::ReadAllBytes($path)
        $entries = @()
        foreach ($entry in $db.RootGroup.GetEntries($true)) {
            $inBin = $false
            $g = $entry.ParentGroup
            while ($g -ne $null) { if ($db.RecycleBinEnabled -and $g.Uuid.Equals($db.RecycleBinUuid)) { $inBin = $true }; $g = $g.ParentGroup }
            $entries += [ordered]@{
                uuid = $entry.Uuid.ToHexString()
                title = $entry.Strings.ReadSafe('Title')
                groupPath = Get-Path $entry.ParentGroup $db.RootGroup
                userName = $entry.Strings.ReadSafe('UserName')
                password = $entry.Strings.ReadSafe('Password')
                url = $entry.Strings.ReadSafe('URL')
                notesLength = $entry.Strings.ReadSafe('Notes').Length
                notesSha256 = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($entry.Strings.ReadSafe('Notes')))).Replace('-', '')
                historyCount = $entry.History.UCount
                inRecycleBin = $inBin
                lastModificationTime = $entry.LastModificationTime.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            }
        }
        return [ordered]@{
            file = $name
            password = $pw
            version = ('{0:X8}' -f [BitConverter]::ToUInt32($bytes, 8))
            kdf = $kdf
            cipher = $cipher
            compression = if ($gzip) { 'gzip' } else { 'none' }
            rootGroupUuid = $db.RootGroup.Uuid.ToHexString()
            recycleBinUuid = if ($db.RecycleBinEnabled) { $db.RecycleBinUuid.ToHexString() } else { $null }
            entries = $entries
        }
    } finally {
        $db.Close()
    }
}

$manifest = [ordered]@{ generator = "KeePass $((Get-Item $KeePass).VersionInfo.FileVersion)"; generated = $Now.ToString('yyyy-MM-dd'); fixtures = @() }

# 1. Rich KDBX 4.0: nested groups, history with protected values before later entries, attachments,
#    custom data/icons, protected notes, a recycle bin with a deleted entry and a deleted-object record.
$db = New-Database 'argon2d-aes-gzip.kdbx' $Password 'argon2d' 'aes' $true
$internet = New-Group $db.RootGroup 'Internet'
$servers = New-Group $db.RootGroup 'Servers'
$linux = New-Group $servers 'Linux'
$github = New-Entry $internet 'GitHub' 'octocat' 'gh-current-7Q' 'https://github.com' 'Primary account'
Add-History $db $github @('gh-oldest-1', 'gh-older-2', 'gh-old-3')
$github.Binaries.Set('recovery-codes.txt', (New-Object KeePassLib.Security.ProtectedBinary($false, [Text.Encoding]::UTF8.GetBytes('attachment bytes are skipped'))))
$github.CustomData.Set('plugin.data', 'skipped')
$null = New-Entry $internet 'Mail' 'atlas@example.test' 'mail-after-history' 'https://mail.example.test' ''
$router = New-Entry $linux 'Router' 'admin' 'r0uter-pw' 'ssh://10.0.0.1' 'protected notes value' $true
Add-History $db $router @('router-old')
$null = New-Entry $db.RootGroup "Unicode $([char]0x2713) Z$([char]0x00E1)znam" "u$([char]0x017E)ivatel" "p$([char]0x0159)$([char]0x00ED)li$([char]0x0161) $([char]0x017E)lu$([char]0x0165)ou$([char]0x010D)k$([char]0x00FD) $([char]0x2603)" '' "Pozn$([char]0x00E1)mka"
$bin = Add-RecycleBin $db
$old = New-Entry $bin 'Old VPN' 'vpn' 'deleted-secret' '' ''
$db.CustomData.Set('meta.plugin', 'skipped')
$db.CustomIcons.Add((New-Object KeePassLib.PwCustomIcon((New-Object KeePassLib.PwUuid($true)), $Png)))
$db.DeletedObjects.Add((New-Object KeePassLib.PwDeletedObject((New-Object KeePassLib.PwUuid($true)), $Now)))
Save-Database $db
$manifest.fixtures += Read-Manifest 'argon2d-aes-gzip.kdbx' $Password $null 'argon2d' 'aes' $true

# 2. KDBX 4.1, Argon2id, ChaCha20, uncompressed, non-ASCII master password.
$db = New-Database 'argon2id-chacha20-none.kdbx' $UnicodePassword 'argon2id' 'chacha20' $false
$tools = New-Group $db.RootGroup 'Tools'
$e = New-Entry $tools 'Jenkins' 'ci' 'jenkins-now' 'https://ci.example.test' 'CI server'
Add-History $db $e @('jenkins-before')
$null = New-Entry $tools 'Nexus' 'deploy' 'nexus-pw' 'https://nexus.example.test' ''
Save-Database $db 0x00040001
$manifest.fixtures += Read-Manifest 'argon2id-chacha20-none.kdbx' $UnicodePassword $null 'argon2id' 'chacha20' $false

# 3. and 4. AES-KDF with both outer ciphers and both compression settings.
$db = New-Database 'aeskdf-aes-none.kdbx' $Password 'aes' 'aes' $false
$null = New-Entry $db.RootGroup 'Database' 'sa' 'aes-kdf-aes' 'mssql://db.example.test' ''
Save-Database $db
$manifest.fixtures += Read-Manifest 'aeskdf-aes-none.kdbx' $Password $null 'aeskdf' 'aes' $false

$db = New-Database 'aeskdf-chacha20-gzip.kdbx' $Password 'aes' 'chacha20' $true
$e = New-Entry $db.RootGroup 'Storage' 'backup' 'aes-kdf-chacha' 'smb://nas.example.test/share' 'NAS'
Add-History $db $e @('aes-kdf-chacha-old')
Save-Database $db
$manifest.fixtures += Read-Manifest 'aeskdf-chacha20-gzip.kdbx' $Password $null 'aeskdf' 'chacha20' $true

# 5. Large notes (below the default 1 MiB field limit), also kept in a history item.
$random = New-Object Random 4242
$chars = 'abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,'
$builder = New-Object Text.StringBuilder
for ($i = 0; $i -lt 700 * 1024; $i++) { [void]$builder.Append($chars[$random.Next($chars.Length)]) }
$db = New-Database 'big-notes.kdbx' $Password 'argon2id' 'aes' $true
$e = New-Entry $db.RootGroup 'Runbook' 'ops' 'big-notes-pw' '' $builder.ToString()
Add-History $db $e @('big-notes-old')
$null = New-Entry $db.RootGroup 'After runbook' 'ops2' 'after-big-notes' '' ''
Save-Database $db
$manifest.fixtures += Read-Manifest 'big-notes.kdbx' $Password $null 'argon2id' 'aes' $true

# 6. Master key changed in KeePass after the stored password was valid.
$db = New-Database 'changed-key.kdbx' 'previous master password' 'argon2id' 'aes' $true
$null = New-Entry $db.RootGroup 'Share' 'user' 'share-pw' '' ''
$db.Save($null)
$db.MasterKey = New-Key $Password $null
Save-Database $db
$fixture = Read-Manifest 'changed-key.kdbx' $Password $null 'argon2id' 'aes' $true
$fixture.previousPassword = 'previous master password'
$manifest.fixtures += $fixture

# 7. Decompression bomb: an 80 MiB zero attachment compresses to a small file.
$db = New-Database 'bomb.kdbx' $Password 'argon2id' 'aes' $true
$e = New-Entry $db.RootGroup 'Bomb' 'x' 'bomb-pw' '' ''
$e.Binaries.Set('zeros.bin', (New-Object KeePassLib.Security.ProtectedBinary($false, (New-Object byte[] (80MB)))))
Save-Database $db
$manifest.fixtures += [ordered]@{ file = 'bomb.kdbx'; password = $Password; decompressedAtLeast = 80MB }

# 8. Group nesting deeper than the default limit of 64.
$db = New-Database 'deep-groups.kdbx' $Password 'argon2id' 'aes' $true
$g = $db.RootGroup
for ($i = 1; $i -le 70; $i++) { $g = New-Group $g "Level $i" }
$null = New-Entry $g 'Deep' 'd' 'deep-pw' '' ''
Save-Database $db
$manifest.fixtures += [ordered]@{ file = 'deep-groups.kdbx'; password = $Password; groupDepth = 70 }

# 9. KDBX 3.1 (unsupported) and 10. password plus key file (unsupported: password-only composite key).
$db = New-Database 'kdbx31.kdbx' $Password 'aes' 'aes' $true
$null = New-Entry $db.RootGroup 'Legacy' 'l' 'legacy-pw' '' ''
Save-Database $db 0x00030001
$manifest.fixtures += [ordered]@{ file = 'kdbx31.kdbx'; password = $Password; version = '00030001' }

$keyFile = Join-Path $env:TEMP "atlas-fixture-$([Guid]::NewGuid().ToString('N')).keyx"
[KeePassLib.Keys.KcpKeyFile]::Create($keyFile, $null)
try {
    $db = New-Database 'keyfile.kdbx' $Password 'argon2id' 'aes' $true $keyFile
    $null = New-Entry $db.RootGroup 'Needs key file' 'k' 'keyfile-pw' '' ''
    Save-Database $db
} finally {
    Remove-Item $keyFile
}
$manifest.fixtures += [ordered]@{ file = 'keyfile.kdbx'; password = $Password; keyFile = $true }

$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $Output 'manifest.json'), $json + "`r`n", (New-Object Text.UTF8Encoding($false)))
Get-ChildItem $Output -Filter *.kdbx | ForEach-Object { '{0,-32} {1,8} bytes' -f $_.Name, $_.Length }
