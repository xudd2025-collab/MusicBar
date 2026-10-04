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
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',('/out:' + (Join-Path $outputPath 'MusicBar.exe')),('/win32manifest:' + (Join-Path $projectDirectory 'app.manifest')))
foreach ($reference in $references) { $arguments += '/r:' + $reference }
$sourceFiles = @('AppUpdateService.cs','AssemblyInfo.cs','LrcParser.cs','LyricController.cs','LyricOverlay.cs','LyricRepository.cs','MainForm.cs','Models.cs','MusicSessionReader.cs','NetEaseBridgeReader.cs','Program.cs','SettingsStore.cs','StartupRegistration.cs','TaskbarGeometry.cs','Theme.cs')
$sourcePaths = $sourceFiles | ForEach-Object { Join-Path $projectDirectory $_ }
$arguments += $sourcePaths
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'MusicBar 编译失败。' }
Copy-Item -LiteralPath (Join-Path $projectDirectory 'MusicBar.exe.config') -Destination $outputPath -Force
if (Test-Path -LiteralPath (Join-Path $projectDirectory 'update-source.json')) { Copy-Item -LiteralPath (Join-Path $projectDirectory 'update-source.json') -Destination $outputPath -Force }
if ($Check) {
    $checkArguments = @('/nologo','/target:exe','/platform:anycpu','/utf8output',('/out:' + (Join-Path $outputPath 'MusicBar.Checks.exe')),('/main:MusicBar.Checks'))
    foreach ($reference in $references) { $checkArguments += '/r:' + $reference }
    $checkArguments += $sourcePaths
    $checkArguments += Join-Path $projectDirectory 'checks\Checks.cs'
    & $compilerPath @checkArguments
    if ($LASTEXITCODE -ne 0) { throw '检查程序编译失败。' }
    & (Join-Path $outputPath 'MusicBar.Checks.exe')
    if ($LASTEXITCODE -ne 0) { throw 'MusicBar 检查失败。' }
    $timingArguments = @('/nologo','/target:exe','/platform:anycpu','/utf8output',('/out:' + (Join-Path $outputPath 'MusicBar.TimingChecks.exe')), '/main:TimelineTimingChecks')
    foreach ($reference in $references) { $timingArguments += '/r:' + $reference }
    $timingArguments += $sourcePaths
    $timingArguments += Join-Path $projectDirectory 'checks\TimelineTimingChecks.cs'
    & $compilerPath @timingArguments
    if ($LASTEXITCODE -ne 0) { throw '播放时间轴检查编译失败。' }
    & (Join-Path $outputPath 'MusicBar.TimingChecks.exe')
    if ($LASTEXITCODE -ne 0) { throw '播放时间轴检查失败。' }
    $bridgeArguments = @('/nologo','/target:exe','/platform:anycpu','/utf8output',('/out:' + (Join-Path $outputPath 'MusicBar.BridgeChecks.exe')), '/main:NetEaseBridgeChecks')
    foreach ($reference in $references) { $bridgeArguments += '/r:' + $reference }
    $bridgeArguments += $sourcePaths
    $bridgeArguments += Join-Path $projectDirectory 'checks\NetEaseBridgeChecks.cs'
    & $compilerPath @bridgeArguments
    if ($LASTEXITCODE -ne 0) { throw '网易云接入检查编译失败。' }
    & (Join-Path $outputPath 'MusicBar.BridgeChecks.exe')
    if ($LASTEXITCODE -ne 0) { throw '网易云接入检查失败。' }
    $updateArguments = @('/nologo','/target:exe','/utf8output',('/out:' + (Join-Path $outputPath 'MusicBar.UpdateChecks.exe')), '/main:MusicBar.UpdateChecks')
    foreach ($reference in $references) { $updateArguments += '/r:' + $reference }
    $updateArguments += $sourcePaths
    $updateArguments += Join-Path $projectDirectory 'checks\UpdateChecks.cs'
    & $compilerPath @updateArguments
    if ($LASTEXITCODE -ne 0) { throw '更新检查编译失败。' }
    & (Join-Path $outputPath 'MusicBar.UpdateChecks.exe')
    if ($LASTEXITCODE -ne 0) { throw '更新检查失败。' }
}
Write-Output (Join-Path $outputPath 'MusicBar.exe')
