param([string]$CompilerPath, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SkipBuild) { & (Join-Path $projectDirectory 'build.ps1'); if ($LASTEXITCODE) { throw '构建失败。' } }
$configPath = Join-Path $projectDirectory 'update-source.json'
if (-not (Test-Path -LiteralPath $configPath)) { throw '请先在 update-source.json 设置 GitHub repository，格式 owner/MusicBar。' }
$repository = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).repository
if ($repository -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$') { throw '更新仓库配置无效。' }
$exePath = Join-Path $projectDirectory 'release\MusicBar.exe'
$version = ([Version](Get-Item -LiteralPath $exePath).VersionInfo.FileVersion).ToString(3)
$distPath = Join-Path $projectDirectory 'dist'
$stagePath = Join-Path $distPath 'stage\MusicBar'
New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
# Stage contains only these explicitly allowed files. User data is never copied.
$files = @('MusicBar.exe','MusicBar.exe.config','MusicBar.ico','update-source.json','LICENSE','README.md')
foreach ($existing in Get-ChildItem -LiteralPath $stagePath -Force) { if ($existing.PSIsContainer -or $existing.Name -notin $files) { throw '分发暂存目录存在额外文件，请检查后使用干净目录。' } }
foreach ($file in $files) {
    $source = if ($file -in @('MusicBar.exe','MusicBar.exe.config','MusicBar.ico')) { Join-Path $projectDirectory ('release\' + $file) } else { Join-Path $projectDirectory $file }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stagePath $file) -Force
}
if (-not $CompilerPath) {
    foreach ($candidate in @((Join-Path $projectDirectory '.tools\inno\ISCC.exe'), "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe")) {
        if (Test-Path -LiteralPath $candidate) { $CompilerPath = $candidate; break }
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) { throw '需要 Inno Setup 6 或 7，请安装官方版本并用 -CompilerPath 指定 ISCC.exe。' }
& $CompilerPath "/DAppVersion=$version" "/DSourceDir=$stagePath" "/DOutputDir=$distPath" (Join-Path $PSScriptRoot 'MusicBar.iss')
if ($LASTEXITCODE -ne 0) { throw '安装包编译失败。' }
$portablePath = Join-Path $distPath "MusicBar-Portable-$version.zip"
Compress-Archive -LiteralPath $stagePath -DestinationPath $portablePath -Force
$installerName = "MusicBar-Setup-$version.exe"
$installerPath = Join-Path $distPath $installerName
$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$baseUrl = "https://github.com/$repository/releases/download/v$version"
$releaseNotes = '查看发布页面了解本次更新内容。'
$changelog = Get-Content -LiteralPath (Join-Path $projectDirectory 'CHANGELOG.md') -Raw
$section = [regex]::Match($changelog, '(?ms)^##\s+' + [regex]::Escape($version) + '[^\r\n]*\r?\n(?<notes>.*?)(?=^##\s|\z)')
if ($section.Success -and $section.Groups['notes'].Value.Trim().Length -gt 0) { $releaseNotes = $section.Groups['notes'].Value.Trim() }
$manifest = [ordered]@{
    version = $version
    notes = $releaseNotes
    release_url = "https://github.com/$repository/releases/tag/v$version"
    installer = [ordered]@{
        name = $installerName
        sha256 = $installerHash
        urls = @("$baseUrl/$installerName", "https://raw.githubusercontent.com/$repository/updates/releases/v$version/$installerName", "https://cdn.jsdelivr.net/gh/$repository@updates/releases/v$version/$installerName", "https://fastly.jsdelivr.net/gh/$repository@updates/releases/v$version/$installerName")
    }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $distPath 'update.json') -Encoding utf8
$checksums = foreach ($asset in @($installerPath, $portablePath)) { '{0}  {1}' -f (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $asset) }
$checksums | Set-Content -LiteralPath (Join-Path $distPath 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Installer: $installerPath"
Write-Output "Portable: $portablePath"
