param([Parameter(Mandatory=$true)][string]$Repository, [string]$GhPath = 'gh', [string]$DistDirectory)
$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$') { throw 'Invalid repository.' }
if (-not $DistDirectory) { $DistDirectory = Join-Path $PSScriptRoot '..\dist' }
$manifestPath = Join-Path $DistDirectory 'update.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = $manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $manifest.installer.name -ne "MusicBar-Setup-$version.exe") { throw 'Invalid manifest version or filename.' }
$file = Join-Path $DistDirectory $manifest.installer.name
if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $manifest.installer.sha256) { throw 'Installer hash mismatch.' }
$release = & $GhPath api "repos/$Repository/releases/tags/v$version" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $release.draft -or -not ($release.assets | Where-Object name -eq $manifest.installer.name)) { throw 'Publish the stable release and installer first.' }
$asset = $release.assets | Where-Object name -eq $manifest.installer.name | Select-Object -First 1
if ($asset.digest -and $asset.digest -ine ('sha256:' + $manifest.installer.sha256)) { throw 'Local installer differs from published release.' }
$main = & $GhPath api "repos/$Repository/git/ref/heads/main" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Cannot find main.' }
$updates = & $GhPath api "repos/$Repository/git/ref/heads/updates" 2>$null | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
    @{ref='refs/heads/updates';sha=$main.object.sha} | ConvertTo-Json -Compress | & $GhPath api "repos/$Repository/git/refs" --input - | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create updates branch.' }
}
function Publish-File([string]$Branch, [string]$Path, [byte[]]$Bytes, [bool]$Immutable) {
    $old = & $GhPath api "repos/$Repository/contents/${Path}?ref=$Branch" 2>$null | ConvertFrom-Json
    $exists = $LASTEXITCODE -eq 0
    $content = [Convert]::ToBase64String($Bytes)
    if ($exists -and $Immutable) {
        $header = [Text.Encoding]::ASCII.GetBytes("blob $($Bytes.Length)`0")
        $buffer = New-Object byte[] ($header.Length + $Bytes.Length)
        [Array]::Copy($header, 0, $buffer, 0, $header.Length)
        [Array]::Copy($Bytes, 0, $buffer, $header.Length, $Bytes.Length)
        $sha = [Security.Cryptography.SHA1]::Create()
        try { $blobHash = [BitConverter]::ToString($sha.ComputeHash($buffer)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
        if ($old.sha -ne $blobHash) { throw "Refusing to overwrite published installer $Path" }
        return
    }
    $request = @{message="Publish update v$version";branch=$Branch;content=$content}
    if ($exists) { $request.sha = $old.sha }
    $request | ConvertTo-Json -Compress | & $GhPath api "repos/$Repository/contents/$Path" --method PUT --input - | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish $Branch/$Path" }
}
Publish-File 'updates' "releases/v$version/$($manifest.installer.name)" ([IO.File]::ReadAllBytes($file)) $true
Publish-File 'main' 'update.json' ([IO.File]::ReadAllBytes($manifestPath)) $false
try {
    Invoke-RestMethod -Uri "https://purge.jsdelivr.net/gh/$Repository@main/update.json" -TimeoutSec 15 | Out-Null
} catch { Write-Warning 'CDN cache refresh was unavailable; GitHub routes already point to the published version.' }
Write-Output "Update metadata and mirror published for v$version."
