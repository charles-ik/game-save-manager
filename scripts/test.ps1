$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
& (Join-Path $repo '.tools\dotnet\dotnet.exe') test (Join-Path $repo 'tests\GameSaveManager.Tests\GameSaveManager.Tests.csproj') --no-restore --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& (Join-Path $repo '.tools\dotnet\dotnet.exe') test (Join-Path $repo 'tests\GameSaveManager.Playnite.Tests\GameSaveManager.Playnite.Tests.csproj') --no-restore --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) { throw 'Playnite integration tests failed.' }
