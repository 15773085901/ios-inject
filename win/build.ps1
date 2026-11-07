$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$zsign = Join-Path $here 'zsign.exe'
if (-not (Test-Path $zsign)) {
    Write-Host 'Downloading zsign.exe ...'
    $zip = Join-Path $env:TEMP 'zsign-windows-x64.zip'
    curl.exe -sL -o $zip 'https://github.com/zhlynn/zsign/releases/download/v1.1.3/zsign-windows-x64.zip'
    Expand-Archive -Path $zip -DestinationPath $here -Force
}

$dylib = Join-Path $here 'libkami.dylib'
if (-not (Test-Path $dylib)) {
    throw 'Missing libkami.dylib. Put the dylib built by the inject-ipa workflow next to this script.'
}

Copy-Item (Join-Path $here '..\ios\kami\index.html') (Join-Path $here 'index.html') -Force

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$zipDll = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression' -Recurse -Filter 'System.IO.Compression.dll' | Select-Object -First 1).FullName
$zipFileDll = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression.FileSystem' -Recurse -Filter 'System.IO.Compression.FileSystem.dll' | Select-Object -First 1).FullName

& $csc /nologo /target:exe /out:"$here\KamiInject.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /r:"$zipDll" /r:"$zipFileDll" `
    /resource:"$here\zsign.exe",zsign.exe `
    /resource:"$here\libkami.dylib",libkami.dylib `
    /resource:"$here\index.html",index.html `
    "$here\KamiInject.cs"

if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }
Write-Host ("Built: " + (Join-Path $here 'KamiInject.exe'))
