param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
$extra = @()
if ($NoRestore) { $extra += '--no-restore' }
foreach ($project in @('src\GameSaveManager.App\GameSaveManager.App.csproj', 'src\GameSaveManager.Playnite\GameSaveManager.Playnite.csproj', 'tests\GameSaveManager.Tests\GameSaveManager.Tests.csproj', 'tests\GameSaveManager.Playnite.Tests\GameSaveManager.Playnite.Tests.csproj')) {
    & $dotnet build (Join-Path $repo $project) --nologo @extra
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
