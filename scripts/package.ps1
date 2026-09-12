$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
$release = Join-Path $repo '.artifacts\release'
$application = Join-Path $release 'GameSaveManager'
New-Item -ItemType Directory -Force $application | Out-Null
& $dotnet publish (Join-Path $repo 'src\GameSaveManager.App\GameSaveManager.App.csproj') -c Release -r win-x64 --self-contained true -o $application --nologo
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
& $dotnet build (Join-Path $repo 'src\GameSaveManager.Playnite\GameSaveManager.Playnite.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Companion build failed.' }
$pluginSource = Join-Path $repo 'src\GameSaveManager.Playnite\bin\Release\net462'
$pluginStage = Join-Path $release 'companion'
New-Item -ItemType Directory -Force $pluginStage | Out-Null
foreach ($file in @('extension.yaml', 'GameSaveManager.Playnite.dll', 'GameSaveManager.Contracts.dll', 'Newtonsoft.Json.dll')) {
    Copy-Item -LiteralPath (Join-Path $pluginSource $file) -Destination $pluginStage -Force
}
Compress-Archive -Path (Join-Path $pluginStage '*') -DestinationPath (Join-Path $release 'companion.zip') -Force
Copy-Item -LiteralPath (Join-Path $release 'companion.zip') -Destination (Join-Path $application 'GameSaveManager.Playnite.pext') -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $application -Force
Copy-Item -LiteralPath (Join-Path $repo 'documentation') -Destination $application -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo 'scripts\install.ps1') -Destination (Join-Path $release 'Install.ps1') -Force
Compress-Archive -Path $application,(Join-Path $release 'Install.ps1') -DestinationPath (Join-Path $release 'GameSaveManager-win-x64.zip') -Force
Get-FileHash -LiteralPath (Join-Path $release 'GameSaveManager-win-x64.zip') -Algorithm SHA256
Write-Output "Release: $release"
