param([switch]$RenderOnly)
$ErrorActionPreference = 'Stop'
$projectDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compilerDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $compilerDirectory 'csc.exe'))) {
    $compilerDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compilerPath = Join-Path $compilerDirectory 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '找不到 .NET Framework 编译器。' }
$outputDirectory = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$executablePath = Join-Path $outputDirectory 'OverlayChecks.exe'
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Runtime.Serialization.dll')
foreach ($assemblyName in @('UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll')) {
    $assemblyPath = Join-Path $compilerDirectory $assemblyName
    if (-not (Test-Path -LiteralPath $assemblyPath)) { $assemblyPath = Join-Path (Join-Path $compilerDirectory 'WPF') $assemblyName }
    if (-not (Test-Path -LiteralPath $assemblyPath)) { throw "缺少 $assemblyName。" }
    $references += $assemblyPath
}
$arguments = @('/nologo', '/target:exe', '/langversion:5', '/main:MusicBar.OverlayChecks', '/utf8output', ('/out:' + $executablePath))
foreach ($reference in $references) { $arguments += '/r:' + $reference }
foreach ($source in @('Models.cs', 'TaskbarGeometry.cs', 'LyricOverlay.cs', 'Theme.cs', 'checks\OverlayChecks.cs')) {
    $arguments += Join-Path $projectDirectory $source
}
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw '歌词浮层检查程序编译失败。' }
Push-Location -LiteralPath $outputDirectory
try {
    if ($RenderOnly) { & $executablePath '--render-only' }
    else { & $executablePath }
    if ($LASTEXITCODE -ne 0) { throw '歌词浮层检查失败。' }
} finally { Pop-Location }
