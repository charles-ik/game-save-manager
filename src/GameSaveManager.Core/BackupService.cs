using System.Text.Json;

namespace GameSaveManager.Core;

public sealed class BackupService(AppSettings settings, StateStore store, Ludusavi engine, IProcessRunner runner)
{
    private readonly SemaphoreSlim operation = new(1, 1);
    public string MachineDirectory => Path.Combine(settings.BackupDirectory, settings.MachineId);
    public string Repository => Path.Combine(MachineDirectory, "Saves");
    public event Action<string>? Progress;

    public async Task<List<SaveStatus>> PreviewAsync(CancellationToken ct = default)
    {
        await operation.WaitAsync(ct);
        try { using var result = await engine.PreviewAsync(Repository, ct: ct); return Ludusavi.ParseStatuses(result, true); }
        finally { operation.Release(); }
    }
    public async Task<List<SaveStatus>> BackUpAsync(IReadOnlyCollection<string> runningTitles, IEnumerable<string>? onlyGames = null, CancellationToken ct = default)
    {
        await operation.WaitAsync(ct);
        var work = Path.Combine(store.DirectoryPath, "work", Guid.NewGuid().ToString("N"));
        try
        {
            if (!Directory.Exists(settings.BackupDirectory)) throw new DirectoryNotFoundException("Choose an available backup folder in Settings first.");
            Progress?.Invoke("Locating changed saves…");
            using var preview = await engine.PreviewAsync(Repository, onlyGames, ct);
            var statuses = Ludusavi.ParseStatuses(preview, true);
            var eligible = new List<string>();
            foreach (var status in statuses)
            {
                if (runningTitles.Any(x => Discovery.TitleKey(x) == Discovery.TitleKey(status.Name))) { status.Status = "Deferred"; status.Detail = "Game is running."; }
                else if (status.Status is "Ready to back up" or "Settings only; saves unverified")
                {
                    if (status.Files.Any(IsBusy)) { status.Status = "Deferred"; status.Detail = "Save files are busy or were just modified."; }
                    else eligible.Add(status.Name);
                }
            }
            if (eligible.Count == 0) return statuses;
            Progress?.Invoke($"Staging backups for {eligible.Count} games…");
            BackupRepository.CopyTree(Repository, work);
            var before = eligible.SelectMany(name => statuses.First(s => s.Name == name).Files).Distinct().Where(File.Exists).ToDictionary(x => x, x => (new FileInfo(x).Length, File.GetLastWriteTimeUtc(x)));
            using var response = await engine.BackupAsync(work, eligible, ct);
            var completed = Ludusavi.ParseStatuses(response, false);
            if (completed.Any(x => x.Status is "Failed" or "Excluded" or "Deferred") || eligible.Any(x => completed.All(s => s.Name != x))) throw new IOException("One or more games failed to back up; the previous backup has been preserved.");
            if (before.Any(x => !File.Exists(x.Key) || (new FileInfo(x.Key).Length, File.GetLastWriteTimeUtc(x.Key)) != x.Value)) throw new IOException("Save files changed during backup. Retry after closing the game.");
            ct.ThrowIfCancellationRequested();
            Progress?.Invoke("Verifying and publishing completed backups…");
            BackupRepository.Publish(work, Repository);
            foreach (var result in completed) { statuses.RemoveAll(x => x.Name == result.Name); statuses.Add(result); }
            Paths.AtomicWrite(Path.Combine(MachineDirectory, "machine.json"), Json.Write(new
            {
                schemaVersion = 1, machineId = settings.MachineId, machineName = Environment.MachineName,
                userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                installations = store.Get("games", () => new List<GameInstallation>()), savedAt = DateTimeOffset.UtcNow
            }));
            store.Log($"Backed up {completed.Count} games locally. Cloud sync unverified.");
            return statuses;
        }
        catch (Exception ex) { store.Log("Backup failed: " + ex.Message); throw; }
        finally { Paths.DeleteOwnedDirectory(work, Path.Combine(store.DirectoryPath, "work")); operation.Release(); }
    }
    private static bool IsBusy(string file)
    {
        try
        {
            if (!File.Exists(file)) return true;
            if (File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddSeconds(-10)) return true;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
    public async Task PinAsync(string repository, BackupVersion version, bool locked, CancellationToken ct = default)
    {
        await operation.WaitAsync(ct);
        var work = Path.Combine(store.DirectoryPath, "work", Guid.NewGuid().ToString("N"));
        try
        {
            BackupRepository.Verify(repository); BackupRepository.CopyTree(repository, work);
            await engine.PinAsync(work, version, locked, ct); BackupRepository.Publish(work, repository);
        }
        finally { Paths.DeleteOwnedDirectory(work, Path.Combine(store.DirectoryPath, "work")); operation.Release(); }
    }
    public async Task<string> RestorePreviewAsync(string repository, BackupVersion version, CancellationToken ct = default)
    {
        await operation.WaitAsync(ct);
        try { BackupRepository.Verify(repository); using var result = await engine.RestoreAsync(repository, version.Game, version.Id, true, ct); return result.RootElement.GetRawText(); }
        finally { operation.Release(); }
    }
    public async Task RestoreAsync(string repository, BackupVersion version, string acceptedPreview, IReadOnlyCollection<string> runningTitles, CancellationToken ct = default)
    {
        await operation.WaitAsync(ct);
        try
        {
            if (runningTitles.Count > 0) throw new InvalidOperationException("Close running games before restoring saves.");
            BackupRepository.Verify(repository);
            using var preview = await engine.RestoreAsync(repository, version.Game, version.Id, true, ct);
            if (preview.RootElement.GetRawText() != acceptedPreview) throw new InvalidOperationException("The restore preview changed. Review destinations again before restoring.");
            var statuses = Ludusavi.ParseStatuses(preview, true);
            if (statuses.Count != 1 || statuses[0].Files.Count + statuses[0].Registry.Count == 0 || statuses[0].Status == "Failed") throw new InvalidDataException("No valid restore targets were found.");
            var targets = statuses[0];
            foreach (var file in targets.Files)
            {
                if (!Path.IsPathFullyQualified(file) || Paths.Within(file, settings.BackupDirectory) || Paths.Within(file, store.DirectoryPath)) throw new InvalidDataException("Unsafe restore destination: " + file);
                for (var parent = Path.GetDirectoryName(file); parent != null; parent = Path.GetDirectoryName(parent))
                    if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Restore destination traverses a link: " + file);
            }
            var safety = Path.Combine(MachineDirectory, "Safety", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            var safetyConfig = Path.Combine(store.DirectoryPath, "safety-config");
            var safetyEngine = new Ludusavi(settings, safetyConfig, runner);
            var rule = new SaveRule { Name = "Before restore - " + version.Game, Files = targets.Files.Where(File.Exists).ToList(), Registry = targets.Registry };
            safetyEngine.Configure([], [rule], isolated: true);
            Directory.CreateDirectory(safety);
            Paths.AtomicWrite(Path.Combine(safety, "restore-journal.json"), Json.Write(new { game = version.Game, source = repository, version = version.Id, files = targets.Files, newlyCreated = targets.Files.Where(x => !File.Exists(x)), state = "Safety backup pending" }));
            if (rule.Files.Count + rule.Registry.Count > 0)
            {
                using var safetyResult = await safetyEngine.BackupAsync(safety, [rule.Name], ct);
                if (Ludusavi.ParseStatuses(safetyResult, false).Any(x => x.Status == "Failed")) throw new IOException("Safety backup failed; restore was not started.");
                BackupRepository.Publish(safety, safety);
            }
            store.Log("Restore safety data: " + safety);
            using var restored = await engine.RestoreAsync(repository, version.Game, version.Id, false, ct);
            if (Ludusavi.ParseStatuses(restored, false).Any(x => x.Status == "Failed")) throw new IOException("Restore partially failed. Safety data: " + safety);
            store.Log("Restored " + version.Game + ". Safety data: " + safety);
        }
        catch (Exception ex) { store.Log("Restore failed: " + ex.Message); throw; }
        finally { operation.Release(); }
    }
}
