$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$toolsDir = Join-Path $repo '.tools'
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
$sdkDir = Join-Path $toolsDir 'dotnet'
if (-not (Test-Path (Join-Path $sdkDir 'dotnet.exe'))) {
    $installer = Join-Path $toolsDir 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Version '10.0.401' -InstallDir $sdkDir -NoPath
    if (-not (Test-Path (Join-Path $sdkDir 'dotnet.exe'))) { throw 'SDK installation failed.' }
}
$engineDir = Join-Path $toolsDir 'ludusavi'
if (-not (Test-Path (Join-Path $engineDir 'ludusavi.exe'))) {
    $archive = Join-Path $toolsDir 'ludusavi-v0.31.0-win64.zip'
    Invoke-WebRequest 'https://github.com/mtkennerly/ludusavi/releases/download/v0.31.0/ludusavi-v0.31.0-win64.zip' -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $engineDir -Force
    Invoke-WebRequest 'https://github.com/mtkennerly/ludusavi/releases/download/v0.31.0/ludusavi-v0.31.0-legal.zip' -OutFile (Join-Path $toolsDir 'ludusavi-legal.zip')
    Expand-Archive -LiteralPath (Join-Path $toolsDir 'ludusavi-legal.zip') -DestinationPath (Join-Path $engineDir 'legal') -Force
}
& (Join-Path $sdkDir 'dotnet.exe') --version
& (Join-Path $engineDir 'ludusavi.exe') --version
