param([switch]$Check, [string]$OutputDirectory = 'release')
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkDirectory 'csc.exe'))) { $frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$compilerPath = Join-Path $frameworkDirectory 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 编译器不存在。需要 .NET Framework 4.8。' }
$metadataDirectory = Join-Path $env:WINDIR 'System32\WinMetadata'
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $OutputDirectory))
if (-not $outputPath.StartsWith($projectDirectory + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw '输出目录必须在本工具项目目录内。' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Xml.dll')
$references += Join-Path $frameworkDirectory 'System.Runtime.dll'
$references += Join-Path $metadataDirectory 'Windows.Foundation.winmd'
$references += Join-Path $metadataDirectory 'Windows.Media.winmd'
foreach ($assemblyName in @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll')) {
    $assemblyPath = Join-Path $frameworkDirectory $assemblyName
    if (-not (Test-Path -LiteralPath $assemblyPath)) { $assemblyPath = Join-Path (Join-Path $frameworkDirectory 'WPF') $assemblyName }
    if (-not (Test-Path -LiteralPath $assemblyPath)) { throw "缺少 $assemblyName" }
    $references += $assemblyPath
}
$iconPath = Join-Path $projectDirectory 'assets\MusicBar.ico'
if (-not (Test-Path -LiteralPath $iconPath)) { throw '缺少 assets\MusicBar.ico，无法构建应用图标。' }
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',('/out:' + (Join-Path $outputPath 'MusicBar.exe')),('/win32manifest:' + (Join-Path $projectDirectory 'app.manifest')),('/win32icon:' + $iconPath),('/resource:' + $iconPath + ',MusicBar.AppIcon.ico'))
foreach ($reference in $references) { $arguments += '/r:' + $reference }
$sourceFiles = @('AppHotkey.cs','AppUpdateService.cs','AssemblyInfo.cs','LrcParser.cs','LyricController.cs','LyricOverlay.cs','LyricRepository.cs','MainForm.cs','Models.cs','MusicSessionReader.cs','NetEaseBridgeReader.cs','NetEaseLaunchIntegration.cs','Program.cs','SettingsStore.cs','StartupRegistration.cs','TaskbarGeometry.cs','Theme.cs')
$sourcePaths = $sourceFiles | ForEach-Object { Join-Path $projectDirectory $_ }
$arguments += $sourcePaths
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'MusicBar 编译失败。' }
Copy-Item -LiteralPath (Join-Path $projectDirectory 'MusicBar.exe.config') -Destination $outputPath -Force
Copy-Item -LiteralPath $iconPath -Destination (Join-Path $outputPath 'MusicBar.ico') -Force
if (Test-Path -LiteralPath (Join-Path $projectDirectory 'update-source.json')) { Copy-Item -LiteralPath (Join-Path $projectDirectory 'update-source.json') -Destination $outputPath -Force }
if ($Check) {
    $checkOutputPath = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory 'checks\.build'))
    New-Item -ItemType Directory -Path $checkOutputPath -Force | Out-Null
    $lyricCheckSources = @('Models.cs','SettingsStore.cs','LrcParser.cs','LyricRepository.cs','LyricController.cs','MusicSessionReader.cs','NetEaseBridgeReader.cs')
    $checkArguments = @('/nologo','/target:exe','/platform:anycpu','/utf8output',('/out:' + (Join-Path $checkOutputPath 'MusicBar.Checks.exe')),('/main:MusicBar.Checks'))
    foreach ($reference in $references) { $checkArguments += '/r:' + $reference }
    foreach ($source in ($lyricCheckSources + @('StartupRegistration.cs'))) { $checkArguments += Join-Path $projectDirectory $source }
    $checkArguments += Join-Path $projectDirectory 'checks\Checks.cs'
    & $compilerPath @checkArguments
    if ($LASTEXITCODE -ne 0) { throw '检查程序编译失败。' }
    & (Join-Path $checkOutputPath 'MusicBar.Checks.exe')
    if ($LASTEXITCODE -ne 0) { throw 'MusicBar 检查失败。' }
    # Detected and removed by Kaspersky on 2026-10-06. Do not rebuild,
    # execute, or rename this check until the detection has been reviewed.
    Write-Warning '时间轴独立检查已暂停：MusicBar.TimingChecks.exe 被卡巴斯基检测，尚未确认是否误报。详情见 SECURITY.md。'
    $bridgeArguments = @('/nologo','/target:exe','/platform:anycpu','/utf8output',('/out:' + (Join-Path $checkOutputPath 'MusicBar.BridgeChecks.exe')), '/main:NetEaseBridgeChecks')
    foreach ($reference in $references) { $bridgeArguments += '/r:' + $reference }
    foreach ($source in ($lyricCheckSources + @('NetEaseLaunchIntegration.cs'))) { $bridgeArguments += Join-Path $projectDirectory $source }
    $bridgeArguments += Join-Path $projectDirectory 'checks\NetEaseBridgeChecks.cs'
    & $compilerPath @bridgeArguments
    if ($LASTEXITCODE -ne 0) { throw '网易云接入检查编译失败。' }
    & (Join-Path $checkOutputPath 'MusicBar.BridgeChecks.exe')
    if ($LASTEXITCODE -ne 0) { throw '网易云接入检查失败。' }
    $qqArguments = @('/nologo','/target:exe','/platform:anycpu','/utf8output',('/out:' + (Join-Path $checkOutputPath 'MusicBar.QQMatchingChecks.exe')), '/main:QQMatchingChecks')
    foreach ($reference in $references) { $qqArguments += '/r:' + $reference }
    foreach ($source in $lyricCheckSources) { $qqArguments += Join-Path $projectDirectory $source }
    $qqArguments += Join-Path $projectDirectory 'checks\QQMatchingChecks.cs'
    & $compilerPath @qqArguments
    if ($LASTEXITCODE -ne 0) { throw 'QQ 歌名匹配检查编译失败。' }
    & (Join-Path $checkOutputPath 'MusicBar.QQMatchingChecks.exe')
    if ($LASTEXITCODE -ne 0) { throw 'QQ 歌名匹配检查失败。' }
    # Kaspersky detected and removed this development executable on 2026-10-05.
    # Preserve its source for review; do not rebuild or execute it until resolved.
    Write-Warning '更新检查已暂停：MusicBar.UpdateChecks.exe 被卡巴斯基检测并删除，尚未确认是否误报。详情见 SECURITY.md。'
}
Write-Output (Join-Path $outputPath 'MusicBar.exe')
