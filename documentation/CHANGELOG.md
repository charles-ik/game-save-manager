# Changelog

## 2026-11-15

- **Improved:** New interface on the built-in Windows 11 dark theme: sidebar navigation with counts, one primary action per page, status dots, empty-state hints, a progress bar while working, and buttons that stay disabled until a row is selected.
- **Fixed:** Backing up selected games (or the automatic backup after a game closes) no longer marks every other game "No saves found".
- **Fixed:** The Backups list is no longer wiped by the first operation after a restart.
- **Fixed:** A confirmed game whose folder returns no longer stays "Missing installation".
- **Added:** Remove missing on the Games page drops games whose installation folder is gone.
- **Added:** Locate unknown saves on the Backups page finds save folders named after a game or its program in Documents, Saved Games and AppData, and adds the ones you confirm as save locations.
- **Improved:** Save-database matching also tries a fuzzy lookup and the game's program names, so "Mount and Blade Warband" finds "Mount & Blade: Warband".
- **Improved:** The Games page shows Playnite status and save status in separate columns.
- **Added:** Search boxes and status filters on the Games, Backups and Needs attention lists. Column sorting now survives background refreshes.
- **Added:** In Review game, clicking an executable candidate fills the launch executable field and copies the path.
- **Added:** Remove on the Games page takes a game out of the app completely: not listed, scanned, backed up or sent to Playnite. Pick "Playnite: Removed" in the status filter and use Review game to bring one back.
- **Added:** Ctrl and Shift select several rows. Exclude, Remove and Back up selected act on all of them.
- **Improved:** Reviewing a game that is already in Playnite queues an IGDB metadata download for it. Downloads that failed under the old companion are retried once after the new companion (0.2.0) connects, and Settings shows a notice while the old one is installed.
- **Fixed:** Tables turned white while an operation was running.
- **Fixed:** Games already in the Playnite library were reset to "Needs review" by every scan. They now show "In Playnite".
- **Fixed:** A game with no known save location no longer fails the whole automatic backup pass. Installers ending in "Setup" are no longer picked as a game's program.
- **Fixed:** Playnite companion metadata downloads failed with "Value cannot be null: bytes". Reinstall the companion from Settings to pick this up.
- **Fixed:** Opening the app while it is already in the tray now shows its window. Row selection survives background refreshes. Activity times are shown in local time. Cancel now works during the Playnite package step.

## 2026-09-12

- **Added:** Windows WPF app, tray lifecycle, SQLite state and per-user startup configuration.
- **Added:** Recursive game discovery, review/exclusion, safe nested grouping and coverage export. See [discovery and Playnite](features/discovery-and-playnite.md).
- **Added:** Playnite companion with idempotent imports, conservative metadata requests, event forwarding and installation remapping.
- **Added:** Ludusavi file/registry integration, staged ZIP publication, integrity catalogs, retention and pinning.
- **Added:** Explicit restore previews, pre-restore safety copies, migration configuration export and Playnite packages. See [backup and recovery](features/backup-and-recovery.md).
- **Added:** Complete routine Playnite backup configuration and a self-contained release with a per-user installer script.
- **Verified:** Real engine integration tests, isolated Playnite SDK tests, WPF render and a read-only scan of the user's game collection. See [validation](validation.md).
- **Improved:** Slow account-wide discovery now retains identified-game results with an explicit incomplete-coverage warning; cancellation and selected-game backup controls are available.
- **Improved:** Recognizable settings-only detections are distinguished from protected progress. Actual save-file copies from three games passed byte-for-byte restore checks. See [local coverage](local-coverage.md).
