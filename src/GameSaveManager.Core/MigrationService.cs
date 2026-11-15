using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace GameSaveManager.Core;

public sealed class MigrationService(AppController app)
{
    public void ConfigureRoutineBackups()
    {
        if (IsPlayniteRunning()) throw new InvalidOperationException("Close Playnite before configuring its startup backups, then click this button again.");
        var path = Path.Combine(app.Settings.PlayniteDataDirectory, "config.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Playnite settings were not found. Open Playnite once and install the companion first.");
        var original = File.ReadAllText(path);
        var config = JsonNode.Parse(original)?.AsObject() ?? throw new InvalidDataException("Playnite configuration is invalid.");
        var folder = Path.Combine(app.Backups.MachineDirectory, "Playnite", "Routine"); Directory.CreateDirectory(folder);
        config["AutoBackupEnabled"] = true; config["AutoBackupFrequency"] = 1; config["AutoBackupDir"] = folder; config["RotatingBackups"] = 3;
        config["AutoBackupIncludeLibFiles"] = true; config["AutoBackupIncludeExtensionsData"] = true; config["AutoBackupIncludeExtensions"] = true; config["AutoBackupIncludeThemes"] = true;
        Paths.AtomicWrite(Path.Combine(app.Store.DirectoryPath, "playnite-settings-before-backup-config.json"), original);
        Paths.AtomicWrite(path, config.ToJsonString(Json.Options));
        app.Store.Log("Configured complete daily Playnite startup backups with three retained packages.");
    }
    public void IndexRoutineBackups()
    {
        var folder = Path.Combine(app.Backups.MachineDirectory, "Playnite", "Routine");
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "PlayniteBackup-*.zip"))
        {
            if (File.Exists(file + ".sha256") || File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddSeconds(-30)) continue;
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    foreach (var entry in archive.Entries) { using var content = entry.Open(); content.CopyTo(Stream.Null); }
                Paths.AtomicWrite(file + ".sha256", BackupRepository.Hash(file));
            }
            catch (IOException) { }
            catch (InvalidDataException) { }
        }
    }
    public async Task<string> PrepareAsync(CancellationToken ct = default)
    {
        var saves = await app.BackupAsync();
        if (saves.Any(x => x.Status == "Failed" || x.Status == "Deferred" && x.Name != "Account-wide discovery")) throw new InvalidOperationException("Some saves were deferred or failed. Close games and complete their backups before preparing a migration.");
        return await app.Exclusive(async () =>
        {
            if (ct == default) ct = app.OperationToken;
            var versions = await app.Engine.VersionsAsync(app.Backups.Repository, ct);
            foreach (var newest in versions.GroupBy(x => x.Game).Select(x => x.First())) await app.Backups.PinAsync(app.Backups.Repository, newest, true, ct);
            var package = await BackupPlayniteAsync(true, ct);
            Paths.AtomicWrite(Path.Combine(app.Backups.MachineDirectory, "app-configuration.json"), Json.Write(new
            {
                schemaVersion = 1, roots = app.Settings.Roots, rules = app.Rules, games = app.Games,
                missingCoverage = saves.Where(x => x.Status is "Unknown location" or "No saves found" or "Deferred" || x.Status.StartsWith("Settings", StringComparison.Ordinal)).Select(x => new { x.Name, x.Status, x.Detail })
            }));
            app.Store.Log("Migration package ready locally. Check OneDrive upload completion before moving PCs.");
            return package;
        });
    }
    public async Task<string> BackupPlayniteAsync(bool pinned, CancellationToken ct = default)
    {
        if (app.RunningTitles().Count > 0) throw new InvalidOperationException("Close your games before backing up Playnite.");
        if (!File.Exists(app.Settings.PlayniteExecutable)) throw new FileNotFoundException("Playnite executable not found. Configure its location in Settings.");
        var directory = Path.Combine(app.Backups.MachineDirectory, "Playnite", pinned ? "Pinned" : "Routine");
        Directory.CreateDirectory(directory);
        var stagedDirectory = Path.Combine(app.Store.DirectoryPath, "playnite-work"); Directory.CreateDirectory(stagedDirectory);
        var staged = Path.Combine(stagedDirectory, "PlayniteBackup-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + ".zip");
        var configuration = Path.Combine(stagedDirectory, "backup.json");
        Paths.AtomicWrite(configuration, Json.Write(new
        {
            DataDir = app.Settings.PlayniteDataDirectory, LibraryDir = app.Settings.PlayniteLibraryDirectory,
            OutputFile = staged, BackupItems = new[] { 0, 1, 2, 3, 4, 5 }, ClosedWhenDone = !IsPlayniteRunning(), CancelIfGameRunning = true
        }));
        StartPlaynite("--backup", configuration);
        await WaitForArchiveAsync(staged, ct);
        var destination = Path.Combine(directory, Path.GetFileName(staged));
        File.Copy(staged, destination + ".partial", true);
        if (BackupRepository.Hash(staged) != BackupRepository.Hash(destination + ".partial")) throw new IOException("Playnite archive copy failed verification.");
        File.Move(destination + ".partial", destination, true);
        Paths.AtomicWrite(destination + ".sha256", BackupRepository.Hash(destination));
        File.Delete(staged);
        if (!pinned)
            foreach (var old in Directory.GetFiles(directory, "PlayniteBackup-*.zip").OrderByDescending(x => x).Skip(3)) { File.Delete(old); File.Delete(old + ".sha256"); }
        app.Store.Log("Playnite package saved: " + destination); return destination;
    }
    public async Task RestorePlayniteAsync(string archive, CancellationToken ct = default)
    {
        if (app.RunningTitles().Count > 0) throw new InvalidOperationException("Close games before restoring Playnite.");
        if (!File.Exists(archive + ".sha256") || BackupRepository.Hash(archive) != File.ReadAllText(archive + ".sha256").Trim()) throw new InvalidDataException("Playnite package is unavailable or failed verification.");
        await BackupPlayniteAsync(true, ct);
        var configuration = Path.Combine(app.Store.DirectoryPath, "playnite-restore.json");
        Paths.AtomicWrite(configuration, Json.Write(new
        {
            BackupFile = archive, DataDir = app.Settings.PlayniteDataDirectory, LibraryDir = app.Settings.PlayniteLibraryDirectory,
            RestoreItems = new[] { 0, 1, 2, 3, 4, 5 }, ClosedWhenDone = false, CancelIfGameRunning = true,
            RestoreLibrarySettingsPath = app.Settings.PlayniteLibraryDirectory
        }));
        StartPlaynite("--restorebackup", configuration);
        app.Store.Log("Playnite restore requested. Check its restore result before reconnecting games.");
    }
    private void StartPlaynite(string command, string configuration)
    {
        var info = new ProcessStartInfo(app.Settings.PlayniteExecutable) { UseShellExecute = false };
        info.ArgumentList.Add(command); info.ArgumentList.Add(configuration);
        Process.Start(info)?.Dispose();
    }
    public static bool IsPlayniteRunning()
    {
        var processes = Process.GetProcessesByName("Playnite.DesktopApp").Concat(Process.GetProcessesByName("Playnite.FullscreenApp")).ToList();
        var any = processes.Count > 0; foreach (var process in processes) process.Dispose(); return any;
    }
    private static async Task WaitForArchiveAsync(string file, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(file))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    foreach (var entry in archive.Entries) { using var content = entry.Open(); content.CopyTo(Stream.Null); }
                    if (archive.Entries.Count > 0) return;
                }
                catch (IOException) { }
                catch (InvalidDataException) { }
            }
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException("Playnite did not complete the backup. Check its window and try again.");
    }
}
