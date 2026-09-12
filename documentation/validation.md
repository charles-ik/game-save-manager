# Validation

## Automated coverage

The test suites use temporary files and an isolated Windows registry key. The Playnite suite substitutes SDK collections; it never opens or changes the real Playnite database.

The current suites contain 24 tests: 20 core/engine cases and four Playnite SDK cases. All projects compile without warnings.

- Actual Ludusavi file backup, restore and recovery from the pre-restore safety copy.
- Actual Windows registry backup and restore using a dedicated fixture key.
- Restore into a different filesystem location.
- Retention of changed versions and permanently pinned backups; unchanged saves do not add versions.
- Rejection of corrupt archives and unsafe ZIP paths.
- Deferral of running games and exclusively locked save files.
- Correct reporting of per-file failures.
- Nested/overlapping root handling; installer and extraction exclusions; Unreal launcher grouping.
- Persistence of custom rules.
- Cancellation terminates child processes promptly, and split installed/account scans merge their results without duplicating file entries.
- Account-scan timeout retains partial results with an explicit warning; user cancellation never masquerades as success, and temporary engine configuration is restored.
- Recognizable graphics-configuration-only results never claim that progress saves are protected.
- Playnite import retry/restart idempotency, preservation of manual metadata/arguments, intentional removal, and validated path remapping.

## Desktop and local-library checks

The WPF app has a smoke-test mode that renders its actual controls into a PNG using isolated state and exits. A separate scan-only mode reads the configured game folders and writes a JSON report. Neither starts tray automation nor contacts a live Playnite instance.

The September 12, 2026 discovery pass found 60 installation candidates under `D:\Games` after filtering redistributables and nested helpers. Names and save coverage still need review; the result is not 60 verified backups.

Actual save-file copies from Lies of P, Spider-Man 2 and Saints Row also passed isolated backup/restore hash comparisons. See [the local coverage report](local-coverage.md).

An unrestricted account-wide save preview stalled on this machine. The integration now limits the additional global pass to 90 seconds and reports partial coverage while retaining identified-installation results. The initial release does not claim complete save coverage of this PC.

## Remaining hands-on validation

- Live IGDB responses and artwork downloads inside an installed Playnite companion.
- Playnite's complete backup/restart/restore UI on a second PC.
- OneDrive synchronization during a long upload or interruption.
- A representative restore of each personally important game in its actual game client.

The build is therefore an experimental first release. These limits are distinct from the automated file/registry recovery tests, which use the real backup engine.
