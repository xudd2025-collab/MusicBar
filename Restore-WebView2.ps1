# Restore only the runtime files needed by this application from Microsoft's
# pinned public NuGet package. Existing verified SDK files are never replaced.
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$sdkVersion = '1.0.2903.40'
$packageSha256 = 'EF128016DD1E51C59178C827ED5B8AA3322C57AFA8675D930F8109505542AD74'
$packageUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$sdkVersion/microsoft.web.webview2.$sdkVersion.nupkg"
$sdkFiles = [ordered]@{
    'lib\net462\Microsoft.Web.WebView2.Core.dll' = '88A3B62F45225A811CDB85DF6DFD95C2BFF9A0E43E3B04F813B125EACA56CC9F'
    'lib\net462\Microsoft.Web.WebView2.WinForms.dll' = '29FC954C3FDE1749817D158F3DD5EBE9EFD3B3CE9708D86092FB1E9F023A1CB0'
    'runtimes\win-x86\native\WebView2Loader.dll' = '6F59AD578FDE1F65C44B6935DAB0901F014B5268E056B930FB34C0919F1BF4B9'
    'runtimes\win-x64\native\WebView2Loader.dll' = '462B36FD1BE6CA9F7563466A89E57C41EF4A4DEF3E0A84FA885D203AEA4A3AAF'
    'runtimes\win-arm64\native\WebView2Loader.dll' = 'CB561AE4537E39BBA5DD8AF1F648F189D9562B1003FD110A9E889ACE527FB4E8'
    'LICENSE.txt' = '0AF8F1B807512AAE39C2AC1AA4D0CAE65CABECB6FD554B8439A5162A0D6ECA55'
    'NOTICE.txt' = '106423785C5B7EBA0A8E61D1837F2132E9C828E20AD530F565D981C1DF60DD90'
}

function Get-SdkChildPath([string]$RelativePath) {
    $resolved = [IO.Path]::GetFullPath((Join-Path $projectDirectory $RelativePath))
    if (-not $resolved.StartsWith($projectDirectory.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WebView2 SDK 路径必须位于项目目录内。'
    }
    $ancestor = $resolved
    while ($ancestor -and -not [string]::Equals($ancestor, $projectDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'WebView2 SDK 路径不能经过目录链接或文件链接。'
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $resolved
}

$sdkRelative = '.tools\webview2\' + $sdkVersion
$sdkDirectory = Get-SdkChildPath $sdkRelative
$missingFiles = @()
foreach ($relative in $sdkFiles.Keys) {
    $file = Get-SdkChildPath ($sdkRelative + '\' + $relative)
    if (Test-Path -LiteralPath $file) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $sdkFiles[$relative]) {
            throw "现有 WebView2 SDK 文件校验失败，未覆盖：$relative"
        }
    } else { $missingFiles += $relative }
}
if ($missingFiles.Count -eq 0) { return }

$packagePath = Get-SdkChildPath ('.tools\webview2\.download-' + [Guid]::NewGuid().ToString('N') + '.nupkg')
$archive = $null
try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $packagePath) -Force | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $packageUrl -OutFile $packagePath -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ine $packageSha256) {
        throw '微软 WebView2 SDK 下载的 SHA256 不匹配，已停止还原。'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    foreach ($relative in $missingFiles) {
        $entry = $archive.GetEntry($relative.Replace('\', '/'))
        if ($null -eq $entry -or $entry.Length -le 0 -or $entry.Length -gt 2MB) {
            throw "微软 WebView2 SDK 缺少有效的必需文件：$relative"
        }
        $destination = Get-SdkChildPath ($sdkRelative + '\' + $relative)
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        $created = $false
        $completed = $false
        try {
            $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $created = $true
            try {
                $inputStream = $entry.Open()
                try { $inputStream.CopyTo($output) } finally { $inputStream.Dispose() }
            } finally { $output.Dispose() }
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $sdkFiles[$relative]) {
                throw "微软 WebView2 SDK 文件校验失败：$relative"
            }
            $completed = $true
        } finally {
            if ($created -and -not $completed -and (Test-Path -LiteralPath $destination -PathType Leaf)) {
                Remove-Item -LiteralPath $destination -Force
            }
        }
    }
} finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if (Test-Path -LiteralPath $packagePath -PathType Leaf) { Remove-Item -LiteralPath $packagePath -Force }
}
