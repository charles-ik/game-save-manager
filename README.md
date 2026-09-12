# Game Save Manager

A Windows tray app for discovering local games, backing up their saves into OneDrive, restoring older saves or moving PCs, and adding confirmed games to Playnite.

**Version 0.1.0 — experimental first release.** Backup/restore is tested with the real Ludusavi engine and isolated file/registry fixtures. Playnite imports and remapping are tested against mocked SDK collections. Live metadata providers and a complete second-PC migration still need hands-on validation.

## Run the app

The packaged build is in `.artifacts/release/GameSaveManager/GameSaveManager.exe`. It includes the .NET runtime and Ludusavi 0.31.0. Run it directly, or extract `GameSaveManager-win-x64.zip` and run its `Install.ps1` to install for your Windows account and create a Start menu shortcut.

1. Open **Settings**. Confirm your game roots and backup destination. `D:\Games` already includes its nested `SteamUnlocked` directory.
2. Save settings. Enable automation when you want background scans, imports and backups; startup registration is applied here. Until then, use manual actions.
3. In **Games**, click **Scan folders**. Review uncertain titles and executable paths. Confirmation is remembered.
4. In **Backups**, click **Find saves** and review coverage. Add custom locations for missing games. Then click **Back up now**.
5. Install the companion from **Settings → Install Playnite companion**, accept Playnite's installation prompt, and restart Playnite. Confirmed imports are processed while both apps are running and automation is enabled.
6. To configure complete routine Playnite backups, close Playnite and click **Configure Playnite routine backups**. This sets daily startup backups with three copies.

Closing the window keeps the tray app running. Choose **Exit** from its tray menu to stop it. The **Retry metadata** button requests another background lookup; uncertain metadata can be selected directly in Playnite.

Local backup completion does not confirm cloud upload. Check the OneDrive desktop app before moving PCs.

## Build and test

Use PowerShell from this directory:

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1
./scripts/test.ps1
./scripts/package.ps1
```

Bootstrap installs .NET SDK 10.0.401 and Ludusavi 0.31.0 inside `.tools`, without replacing your system SDK. Builds restore pinned NuGet packages. Tests create temporary files and a dedicated `HKCU\Software\GameSaveManager.Tests` child key, then clean them up. They do not change your actual game saves or Playnite library.

For development, launch the executable in `src/GameSaveManager.App/bin/Debug/net10.0-windows` with `DOTNET_ROOT` set to this repo's `.tools/dotnet` directory. The packaged release does not need that environment variable.

## Documentation

See [the documentation index](documentation/README.md), especially [backup and recovery](documentation/features/backup-and-recovery.md), before restoring or moving computers.

The application never executes a discovered game to identify it. Save-location coverage is not guaranteed; unknown and missing saves remain visible. Game installation files, automatic two-way sync, and cross-platform conversion are outside this release.
