using System.Diagnostics;
using GameSaveManager.Contracts;

namespace GameSaveManager.Core;

public sealed class AppController : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly PlayniteBridge bridge = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? currentOperation;
    public CancellationToken OperationToken => currentOperation?.Token ?? lifetime.Token;
    public void CancelCurrentOperation() { try { currentOperation?.Cancel(); SetStatus("Stopping the current operation…"); } catch (ObjectDisposedException) { } }
    private HashSet<string> previousRunning = [];
    private volatile bool scanPending = true;
    private DateTimeOffset changedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset nextAutomationAttempt = DateTimeOffset.MinValue;
    public StateStore Store { get; }
    public AppSettings Settings { get; }
    public List<GameInstallation> Games { get; private set; }
    public List<SaveRule> Rules { get; private set; }
    public List<SaveStatus> Saves { get; private set; } = [];
    public Ludusavi Engine { get; }
    public BackupService Backups { get; }
    public string Status { get; private set; } = "Ready. Scan your folders to get started.";
    public event Action? Changed;
    public event Action<string>? NeedsAttention;
    public AppController(string stateDirectory)
    {
        Store = new(stateDirectory); Settings = Store.Get("settings", () => new AppSettings());
        if (!File.Exists(Settings.EnginePath))
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, ".tools", "ludusavi", "ludusavi.exe");
                if (File.Exists(candidate)) { Settings.EnginePath = candidate; break; }
            }
        }
        Store.Set("settings", Settings);
        Games = Store.Get("games", () => new List<GameInstallation>()); Rules = Store.Get("rules", () => new List<SaveRule>());
        var runner = new ProcessRunner();
        Engine = new(Settings, Path.Combine(stateDirectory, "engine-config"), runner);
        Backups = new(Settings, Store, Engine, runner); Backups.Progress += SetStatus;
        bridge.Handle = HandleBridgeAsync;
    }
    public void Start() { bridge.Start(); Watch(); _ = BackgroundAsync(); }
    public event Action<string>? StatusChanged;
    private void SetStatus(string value) { Status = value; StatusChanged?.Invoke(value); }
    public async Task<T> Exclusive<T>(Func<Task<T>> action)
    {
        await gate.WaitAsync(lifetime.Token);
        currentOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try { return await action(); }
        catch (Exception ex) { Store.Log(ex.Message); SetStatus(ex.Message); throw; }
        finally { try { Store.Set("save-statuses", Saves); } finally { currentOperation.Dispose(); currentOperation = null; gate.Release(); } Changed?.Invoke(); }
    }
    public void SaveSettings()
    {
        Settings.Roots = Paths.DistinctRoots(Settings.Roots);
        if (Settings.Roots.Any(x => Paths.Within(Settings.BackupDirectory, x) || Paths.Within(x, Settings.BackupDirectory))) throw new InvalidOperationException("Keep the backup folder separate from watched game folders.");
        Store.Set("settings", Settings); Watch(); scanPending = true;
    }
    private void PersistGames() { Store.Set("games", Games); }
    public Task<List<GameInstallation>> ScanAsync() => Exclusive(ScanInternalAsync);
    private async Task<List<GameInstallation>> ScanInternalAsync()
    {
        SetStatus("Scanning game folders…");
        var found = await Task.Run(() => new Discovery().Scan(Settings.Roots, Store.Log, OperationToken));
        foreach (var known in Games) known.Present = false;
        foreach (var game in found)
        {
            var old = Games.FirstOrDefault(x => Paths.Equal(x.InstallDirectory, game.InstallDirectory));
            if (old == null) Games.Add(game);
            else
            {
                old.Present = true; old.ExecutableCandidates = game.ExecutableCandidates; old.LastChangeUtc = game.LastChangeUtc;
                if (!old.Confirmed && !old.Excluded) { old.Executable = game.Executable; old.Status = game.Status; old.Reason = game.Reason; }
                if (old.Confirmed && !File.Exists(old.Executable)) { old.Status = "Needs review"; old.Reason = "The confirmed executable is missing."; }
            }
        }
        foreach (var missing in Games.Where(x => !x.Present && !x.Excluded)) missing.Status = "Missing installation";
        if (changedAt > DateTimeOffset.UtcNow.AddMinutes(-2))
            foreach (var waiting in Games.Where(x => x.Present && !x.Confirmed && !x.Excluded))
            { waiting.Status = "Waiting for files"; waiting.Reason = "Waiting for two minutes without changes in watched folders."; }
        ConfigureEngine();
        await RefreshManifestInternalAsync();
        foreach (var game in Games.Where(x => x.Present && !x.Excluded && x.SaveTitle == null))
        {
            var matched = await Engine.ExactTitleAsync(game.Title, OperationToken);
            game.SaveTitle = matched;
            if (matched != null && game.Status != "Waiting for files" && Discovery.ExecutableSupportsTitle(game))
            {
                game.Title = matched; game.Confirmed = true; game.Status = game.PlayniteId == null ? "Queued for Playnite" : "In Playnite";
                game.Reason = "Save database title and executable metadata agree.";
            }
        }
        PersistGames(); ConfigureEngine(); scanPending = Games.Any(x => x.Status == "Waiting for files");
        SetStatus($"Found {Games.Count(x => x.Present)} installations. {Games.Count(x => x.Status == "Needs review")} need review.");
        return Games;
    }
    public void ConfigureEngine(IEnumerable<PathRedirect>? redirects = null) => Engine.Configure(Games, Rules, redirects);
    private async Task RefreshManifestInternalAsync()
    {
        if (Settings.LastManifestUpdate > DateTimeOffset.UtcNow.AddDays(-1)) return;
        try
        {
            SetStatus("Updating the save-location database…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(OperationToken); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await Engine.UpdateManifestAsync(timeout.Token); Settings.LastManifestUpdate = DateTimeOffset.UtcNow; Store.Set("settings", Settings);
        }
        catch (Exception ex) { Store.Log("Save database could not be updated; using cached data where available. " + ex.Message); }
    }
    public Task<List<SaveStatus>> PreviewSavesAsync() => Exclusive(async () =>
    {
        ConfigureEngine(); await RefreshManifestInternalAsync(); Saves = await Backups.PreviewAsync(OperationToken);
        AddCoverageGaps(); SetStatus($"Save scan complete: {Saves.Count} entries."); return Saves;
    });
    private void AddCoverageGaps()
    {
        if (Engine.LastScanWarning != null)
        {
            Saves.Add(new() { Name = "Account-wide discovery", Status = "Deferred", Detail = Engine.LastScanWarning });
            Store.Log(Engine.LastScanWarning);
            if (Store.Get("last-coverage-warning", () => "") != Engine.LastScanWarning) NeedsAttention?.Invoke(Engine.LastScanWarning);
            Store.Set("last-coverage-warning", Engine.LastScanWarning);
        }
        else Store.Set("last-coverage-warning", "");
        foreach (var game in Games.Where(x => x.Present && !x.Excluded))
            if (!Saves.Any(x => x.Name == (game.SaveTitle ?? game.Title))) Saves.Add(new() { Name = game.SaveTitle ?? game.Title, Status = game.SaveTitle == null ? "Unknown location" : "No saves found", Detail = "Add a confirmed custom save rule or play once and scan again." });
    }
    public Task<List<SaveStatus>> BackupAsync(IEnumerable<string>? names = null) => Exclusive(async () =>
    {
        ConfigureEngine(); await RefreshManifestInternalAsync(); Saves = await Backups.BackUpAsync(RunningTitles(), names, OperationToken);
        AddCoverageGaps(); SetStatus("Backup pass finished. Cloud sync unverified."); return Saves;
    });
    public void ConfirmGame(string id, string title, string executable, string arguments)
    {
        if (string.IsNullOrWhiteSpace(title) || !File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Enter a title and select an existing game executable.");
        var game = Games.Single(x => x.Id == id);
        if (game.Title != title) game.SaveTitle = null;
        game.Title = title.Trim(); game.Executable = Paths.Canonical(executable); game.Arguments = arguments; game.Confirmed = true; game.Excluded = false;
        game.Status = game.PlayniteId == null ? "Queued for Playnite" : "In Playnite"; game.Reason = "Confirmed by you."; PersistGames();
    }
    public void ExcludeGame(string id) { var game = Games.Single(x => x.Id == id); game.Excluded = true; game.Status = "Excluded"; PersistGames(); }
    public void SaveRule(SaveRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Files.Count + rule.Registry.Count == 0) throw new ArgumentException("Provide a game name and at least one save path or registry key.");
        foreach (var file in rule.Files)
        {
            if (!Path.IsPathFullyQualified(file) || (!File.Exists(file) && !Directory.Exists(file))) throw new ArgumentException("Select existing, absolute save paths: " + file);
            if (Paths.Within(file, Settings.BackupDirectory) || Paths.Within(Settings.BackupDirectory, file) || Paths.Within(Store.DirectoryPath, file)) throw new ArgumentException("A save rule cannot include app state or backups.");
        }
        foreach (var registry in rule.Registry)
            if (!(registry.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase) || registry.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Use a full HKEY_CURRENT_USER or HKEY_LOCAL_MACHINE registry path.");
        Rules.RemoveAll(x => x.Name == rule.Name); Rules.Add(rule); Store.Set("rules", Rules);
        foreach (var game in Games.Where(x => x.Title == rule.Name)) game.SaveTitle = rule.Name;
        PersistGames(); ConfigureEngine();
    }
    public HashSet<string> RunningTitles()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var possible = Games.Where(g => g.ExecutableCandidates.Append(g.Executable).Any(e => string.Equals(Path.GetFileNameWithoutExtension(e), process.ProcessName, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (possible.Count == 0) continue;
                    string? executable; try { executable = process.MainModule?.FileName; } catch { executable = null; }
                    foreach (var game in possible.Where(g => executable == null || Paths.Within(executable, g.InstallDirectory))) running.Add(game.SaveTitle ?? game.Title);
                }
                catch (InvalidOperationException) { }
            }
        }
        var snapshot = Store.Get("playnite-snapshot", () => new List<LibraryGame>());
        if (Store.Get("last-playnite-poll", () => DateTimeOffset.MinValue) > DateTimeOffset.UtcNow.AddSeconds(-45))
            foreach (var game in snapshot.Where(x => x.IsRunning)) running.Add(Games.FirstOrDefault(x => x.PlayniteId == game.Id)?.SaveTitle ?? game.Name);
        return running;
    }
    private async Task<BridgeMessage> HandleBridgeAsync(BridgeMessage request)
    {
        return await Exclusive(() =>
        {
            if (request.Kind == "poll")
            {
                Store.Set("playnite-snapshot", request.Games); Store.Set("last-playnite-poll", DateTimeOffset.UtcNow);
                if (request.DataDirectory.Length > 0) Settings.PlayniteDataDirectory = request.DataDirectory;
                if (request.LibraryDirectory.Length > 0) Settings.PlayniteLibraryDirectory = request.LibraryDirectory;
                if (request.Executable.Length > 0) Settings.PlayniteExecutable = request.Executable;
                Store.Set("settings", Settings);
                foreach (var game in Games.Where(x => x.PlayniteId == null))
                {
                    var match = request.Games.FirstOrDefault(x => x.InstallDirectory.Length > 0 && Paths.Equal(x.InstallDirectory, game.InstallDirectory) || x.Executable.Length > 0 && game.Executable.Length > 0 && Paths.Equal(x.Executable, game.Executable));
                    if (match != null) { game.PlayniteId = match.Id; game.Status = "In Playnite"; }
                }
                PersistGames();
                var remap = Store.Get("pending-remap", () => new List<LibraryGame>());
                if (remap.Count > 0) return Task.FromResult(new BridgeMessage { Kind = "remap", Games = remap });
                var metadata = Store.Get<LibraryGame?>("pending-metadata", () => null);
                if (metadata != null) return Task.FromResult(new BridgeMessage { Kind = "metadata", Game = metadata });
                var pending = Settings.AutomationEnabled ? Games.FirstOrDefault(x => x.Confirmed && x.Present && !x.Excluded && x.PlayniteId == null && x.Status == "Queued for Playnite" && File.Exists(x.Executable)) : null;
                if (pending != null) return Task.FromResult(new BridgeMessage { Kind = "import", Game = new() { SourceId = pending.Id, Name = pending.Title, InstallDirectory = pending.InstallDirectory, Executable = pending.Executable, Arguments = pending.Arguments } });
            }
            else if (request.Kind == "imported" && request.Game != null)
            {
                var game = Games.FirstOrDefault(x => x.Id == request.Game.SourceId);
                if (game != null) { game.PlayniteId = request.Game.Id; game.Status = "In Playnite"; PersistGames(); Store.Log("Imported into Playnite: " + game.Title); }
            }
            else if (request.Kind == "remapped")
            {
                Store.Set("pending-remap", new List<LibraryGame>()); Store.Log("Playnite installation paths reconnected.");
            }
            else if (request.Kind is "metadataUpdated" or "importFailed" && request.Game != null)
            {
                var game = Games.FirstOrDefault(x => x.Id == request.Game.SourceId || x.PlayniteId == request.Game.Id);
                if (game != null)
                {
                    game.Reason = request.Error.Length == 0 ? "Metadata downloaded." : request.Error;
                    game.Status = request.Error.Length == 0 ? "In Playnite" : request.Kind == "importFailed" ? "Needs review" : "Metadata needs review";
                    PersistGames();
                }
                if (request.Kind == "metadataUpdated") Store.Set<LibraryGame?>("pending-metadata", null);
                Store.Log(request.Error.Length == 0 ? "Metadata downloaded for " + request.Game.Name : request.Error);
            }
            else if (request.Kind == "gameStopped" && request.Game != null && Settings.AutomationEnabled)
            {
                var name = Games.FirstOrDefault(x => x.PlayniteId == request.Game.Id)?.SaveTitle ?? request.Game.Name;
                _ = DelayedBackupAsync([name]);
            }
            return Task.FromResult(new BridgeMessage { Kind = "ok" });
        });
    }
    private async Task DelayedBackupAsync(IEnumerable<string> names)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token); await BackupAsync(names); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { NeedsAttention?.Invoke(ex.Message); }
    }
    private void Watch()
    {
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
        foreach (var root in Settings.Roots.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Changed += FileChanged; watcher.Created += FileChanged; watcher.Deleted += FileChanged; watcher.Renamed += FileChanged;
            watcher.Error += (_, _) => { scanPending = true; changedAt = DateTimeOffset.UtcNow; }; watcher.EnableRaisingEvents = true; watchers.Add(watcher);
        }
    }
    private void FileChanged(object sender, FileSystemEventArgs args) { scanPending = true; changedAt = DateTimeOffset.UtcNow; }
    private async Task BackgroundAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(lifetime.Token)) return;
                new MigrationService(this).IndexRoutineBackups();
                if (!Settings.AutomationEnabled) continue;
                if (DateTimeOffset.UtcNow < nextAutomationAttempt) continue;
                if (scanPending && changedAt < DateTimeOffset.UtcNow.AddMinutes(-2)) await ScanAsync();
                var running = RunningTitles(); var stopped = previousRunning.Except(running).ToArray(); previousRunning = running;
                if (stopped.Length > 0) await BackupAsync(stopped);
                if (Settings.LastDailyBackup == null || Settings.LastDailyBackup < DateTimeOffset.UtcNow.AddDays(-1))
                {
                    await ScanAsync(); await BackupAsync();
                    Settings.LastDailyBackup = DateTimeOffset.UtcNow; Store.Set("settings", Settings);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { nextAutomationAttempt = DateTimeOffset.UtcNow.AddMinutes(15); Store.Log("Background operation: " + ex.Message); NeedsAttention?.Invoke(ex.Message); }
        }
    }
    public void Dispose() { lifetime.Cancel(); bridge.Dispose(); foreach (var watcher in watchers) watcher.Dispose(); }
}
