$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$report = Get-Content -LiteralPath (Join-Path $repo '.artifacts\save-coverage.json') -Raw | ConvertFrom-Json
$engine = Join-Path $repo '.tools\ludusavi\ludusavi.exe'
$validationRoot = Join-Path $repo '.artifacts\real-save-validation'
New-Item -ItemType Directory -Force $validationRoot | Out-Null
$results = @()
foreach ($gameName in @('Lies of P', "Marvel's Spider-Man 2", 'Saints Row: The Third Remastered')) {
    $game = $report.saves | Where-Object Name -eq $gameName | Select-Object -First 1
    $candidate = $game.Files | Where-Object { ($_ -match '(Account_1\.sav|slot1-manual-0\.save|sr3save_00\.sr3s_pc)$') -and (Test-Path -LiteralPath $_ -PathType Leaf) -and (Get-Item -LiteralPath $_).Length -lt 10485760 } | Select-Object -First 1
    if (-not $candidate) { throw "No suitable file was found for $gameName." }
    $work = Join-Path $validationRoot ([Guid]::NewGuid().ToString('N'))
    $source = Join-Path $work 'copied-source'
    $restored = Join-Path $work 'restored-copy'
    $configuration = Join-Path $work 'config'
    $backups = Join-Path $work 'backups'
    New-Item -ItemType Directory -Force $source,$restored,$configuration | Out-Null
    $copiedFile = Join-Path $source ([IO.Path]::GetFileName($candidate))
    Copy-Item -LiteralPath $candidate -Destination $copiedFile
    $settings = @{ roots = @(); customGames = @(@{ name = 'Copy validation'; files = @($source); integration = 'override' }); redirects = @(@{ kind = 'restore'; source = $source; target = $restored }) }
    $settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $configuration 'config.yaml')
    '{}' | Set-Content -LiteralPath (Join-Path $configuration 'manifest.yaml')
    $backupResult = & $engine --config $configuration --no-manifest-update backup --api --force --no-cloud-sync --format zip --path $backups -- 'Copy validation'
    if ($LASTEXITCODE -ne 0) { throw "Copy backup failed: $backupResult" }
    $versions = (& $engine --config $configuration --no-manifest-update backups --api --path $backups) | ConvertFrom-Json
    $version = $versions.games.'Copy validation'.backups[0].name
    if (-not $version) { throw 'No backup version was created.' }
    $preview = (& $engine --config $configuration --no-manifest-update restore --preview --api --no-cloud-sync --path $backups --backup $version -- 'Copy validation') | ConvertFrom-Json
    foreach ($target in $preview.games.'Copy validation'.files.PSObject.Properties.Name) {
        if (-not ([IO.Path]::GetFullPath($target)).StartsWith([IO.Path]::GetFullPath($restored) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Validation restore escaped its isolated destination.' }
    }
    $restoreResult = & $engine --config $configuration --no-manifest-update restore --api --force --no-cloud-sync --path $backups --backup $version -- 'Copy validation'
    if ($LASTEXITCODE -ne 0) { throw "Copy restore failed: $restoreResult" }
    $restoredFile = Join-Path $restored ([IO.Path]::GetFileName($copiedFile))
    $expected = (Get-FileHash -LiteralPath $copiedFile -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath $restoredFile -Algorithm SHA256).Hash
    if ($expected -ne $actual) { throw "Round-trip hash mismatch for $gameName." }
    $results += [pscustomobject]@{ game = $gameName; verified = $true; bytes = (Get-Item -LiteralPath $copiedFile).Length; sha256 = $actual }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $validationRoot 'results.json')
$results | Format-Table game,verified,bytes
