$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'GameSaveManager'
if (-not (Test-Path -LiteralPath (Join-Path $source 'GameSaveManager.exe'))) { throw 'Extract the release ZIP before running Install.ps1.' }
$running = Get-Process -Name 'GameSaveManager' -ErrorAction SilentlyContinue
if ($running) { throw 'Exit Game Save Manager from its tray menu before installing an update.' }
$destination = Join-Path $env:LOCALAPPDATA 'GameSaveManager\App'
New-Item -ItemType Directory -Force $destination | Out-Null
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $destination -Recurse -Force
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Game Save Manager.lnk'))
$shortcut.TargetPath = Join-Path $destination 'GameSaveManager.exe'
$shortcut.WorkingDirectory = $destination
$shortcut.Save()
Write-Output 'Installed for your Windows account. Open Game Save Manager from Start, then save your settings and install the Playnite companion from the app.'
