# Opens a KDBX file with KeePass 2 (KeePassLib inside KeePass.exe) and prints what KeePass reads as JSON.
# Used by the optional KeePass round-trip test of the Atlas writer; the master password is passed in the
# ATLAS_KDBX_PASSWORD environment variable so it never appears on a command line. Development tool only.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$KeePass = 'C:\Program Files\KeePass Password Safe 2\KeePass.exe'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom($KeePass)
$key = New-Object KeePassLib.Keys.CompositeKey
$key.AddUserKey((New-Object KeePassLib.Keys.KcpPassword($env:ATLAS_KDBX_PASSWORD)))
$db = New-Object KeePassLib.PwDatabase
$db.Open([KeePassLib.Serialization.IOConnectionInfo]::FromPath($Path), $key, $null)
try {
    [ordered]@{
        name = $db.Name
        rootGroupUuid = $db.RootGroup.Uuid.ToHexString()
        rootGroupName = $db.RootGroup.Name
        recycleBinEnabled = $db.RecycleBinEnabled
        recycleBinUuid = $db.RecycleBinUuid.ToHexString()
        groups = $db.RootGroup.GetGroups($true).UCount
        entries = $db.RootGroup.GetEntries($true).UCount
        kdf = $db.KdfParameters.KdfUuid.ToHexString()
        cipher = $db.DataCipherUuid.ToHexString()
        compression = $db.Compression.ToString()
    } | ConvertTo-Json -Compress
} finally {
    $db.Close()
}
