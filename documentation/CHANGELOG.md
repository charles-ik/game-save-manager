# Changelog

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
