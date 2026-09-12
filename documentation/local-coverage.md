# Initial local coverage check

**Date:** September 12, 2026. This was a preview, not a backup of the live saves.

The app found 60 installation candidates beneath `D:\Games`, including its nested collections. After identifying games and checking save locations, the preview returned:

| Result | Count |
| --- | ---: |
| Titles with located game data | 20 |
| Unknown save-location mapping | 27 |
| Recognized title, no files located | 12 |
| Additional account-wide scan incomplete | 1 |

Game data includes configuration files. For example, the initial GRID 2 and F1 2014 matches contained hardware settings, not verified progress saves. The app now labels recognizable configuration-only results separately and provides an Inspect files action. Counts are not a percentage of protected installations: multiple installations can share one save identity.

## Isolated checks with actual save-file copies

Copies of these files were backed up and restored into separate temporary project folders. SHA-256 hashes matched before and after each round trip:

| Game | Copied file | Bytes |
| --- | --- | ---: |
| Lies of P | Account_1.sav | 3,024 |
| Marvel's Spider-Man 2 | slot1-manual-0.save | 752,872 |
| Saints Row: The Third Remastered | sr3save_00.sr3s_pc | 108,776 |

This verifies byte preservation for these files, not loading them in each game's client on a different PC. The original live files and the Playnite library were not changed.

The raw preview and copy-validation results are in `.artifacts/save-coverage.json` and `.artifacts/real-save-validation/results.json`. They stay local and are excluded from source control.
