using System.IO.Compression;
using System.Text.Json;
using GameSaveManager.Core;
using Microsoft.Win32;

namespace GameSaveManager.Tests;

public sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "GameSaveManager.Tests", Guid.NewGuid().ToString("N"));
    public string EnginePath { get; }
    public TestWorkspace()
    {
        Directory.CreateDirectory(Root);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !Directory.Exists(Path.Combine(repo.FullName, ".tools"))) repo = repo.Parent;
        EnginePath = Path.Combine(repo?.FullName ?? throw new InvalidOperationException("Run bootstrap before tests."), ".tools", "ludusavi", "ludusavi.exe");
    }
    public string Folder(string relative) { var path = Path.Combine(Root, relative); Directory.CreateDirectory(path); return path; }
    public string FileAt(string relative, string value)
    {
        var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5)); Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(path)!, DateTime.UtcNow.AddMinutes(-5)); return path;
    }
    public (AppSettings Settings, Ludusavi Engine, BackupService Service) Services(string name = "Fixture Game", string? savePath = null)
    {
        var settings = new AppSettings { EnginePath = EnginePath, BackupDirectory = Folder("cloud"), MachineId = "Fixture-PC", Retention = 3, Roots = [] };
        var runner = new ProcessRunner(); var engine = new Ludusavi(settings, Folder("config"), runner);
        engine.Configure([], [new() { Name = name, Files = [savePath ?? Folder("saves")] }], isolated: true);
        return (settings, engine, new(settings, new StateStore(Folder("state")), engine, runner));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Paths.DeleteOwnedDirectory(Root, Path.Combine(Path.GetTempPath(), "GameSaveManager.Tests")); }
}

public sealed class CoreTests
{
    [Fact]
    public void OverlappingRootsAndSiblingPrefixesAreHandled()
    {
        Assert.Equal([@"D:\Games", @"D:\GamesOld"], Paths.DistinctRoots([@"D:\Games\SteamUnlocked", @"D:\Games", @"d:\games", @"D:\GamesOld"]));
        Assert.False(Paths.Within(@"D:\GamesOld", @"D:\Games"));
        Assert.Throws<InvalidDataException>(() => Paths.SafeChild(@"C:\Backup", @"..\outside"));
    }
    [Theory]
    [InlineData("GRID 2", "GRID 2")]
    [InlineData("Super.Woden.GP.2.v1.31b", "Super Woden GP 2")]
    [InlineData("GRID Autosport - Complete Edition", "GRID Autosport - Complete Edition")]
    [InlineData("Lies of P", "Lies of P")]
    public void TitlesPreserveSequelsAndEditions(string source, string expected) => Assert.Equal(expected, Discovery.NormalizeTitle(source));

    [Fact]
    public void NestedLibrariesSkipInstallersAndExtractionDirectories()
    {
        using var work = new TestWorkspace();
        var root = work.Folder("Games");
        work.FileAt("Games/Collection/GRID 2/GRID 2/grid2.exe", "fixture");
        work.FileAt("Games/Collection/GRID 2/Redist/setup.exe", "fixture");
        work.FileAt("Games/Collection/GRID 2/_Redist/helper.exe", "fixture");
        work.FileAt("Games/.extracting-id/unready.exe", "fixture");
        var found = new Discovery().Scan([root, Path.Combine(root, "Collection")], _ => { });
        var game = Assert.Single(found); Assert.Equal("GRID 2", game.Title); Assert.Single(game.ExecutableCandidates);
        Assert.False(game.Confirmed);
    }
    [Fact]
    public void UnrealLaunchersAndNestedBinariesFormOneGame()
    {
        using var work = new TestWorkspace(); var root = work.Folder("Games");
        work.FileAt("Games/Lies of P/LiesofP.exe", "fixture");
        work.FileAt("Games/Lies of P/LiesofP/Binaries/Win64/LiesofP-Win64-Shipping.exe", "fixture");
        work.FileAt("Games/Lies of P/Engine/helper.exe", "fixture");
        var game = Assert.Single(new Discovery().Scan([root], _ => { }));
        Assert.Equal("Lies of P", game.Title); Assert.Equal(2, game.ExecutableCandidates.Count); Assert.EndsWith("LiesofP.exe", game.Executable);
    }
    [Fact]
    public void StateSurvivesReopen()
    {
        using var work = new TestWorkspace();
        var store = new StateStore(work.Folder("state")); store.Set("rules", new[] { new SaveRule { Name = "A", Files = [@"D:\save"] } });
        Assert.Equal("A", new StateStore(work.Folder("state")).Get("rules", () => Array.Empty<SaveRule>())[0].Name);
    }
    [Fact]
    public async Task RealEngineBacksUpRestoresAndPreservesSafetyCopy()
    {
        using var work = new TestWorkspace();
        var save = work.FileAt("saves/profile.sav", "level 7"); var (_, engine, service) = work.Services();
        var preview = await service.PreviewAsync(); Assert.Single(preview); Assert.Equal("Ready to back up", preview[0].Status);
        var result = await service.BackUpAsync([]); Assert.Equal("Backed up", Assert.Single(result).Status);
        BackupRepository.Verify(service.Repository);
        var version = Assert.Single(await engine.VersionsAsync(service.Repository));
        work.FileAt("saves/profile.sav", "level 99");
        var restorePreview = await service.RestorePreviewAsync(service.Repository, version);
        await service.RestoreAsync(service.Repository, version, restorePreview, []);
        Assert.Equal("level 7", File.ReadAllText(save));
        var safety = Assert.Single(Directory.GetDirectories(Path.Combine(service.MachineDirectory, "Safety")));
        BackupRepository.Verify(safety);
        var safeVersion = Assert.Single(await engine.VersionsAsync(safety));
        Assert.Contains("Before restore", safeVersion.Game);
        var safetyPreview = await service.RestorePreviewAsync(safety, safeVersion);
        await service.RestoreAsync(safety, safeVersion, safetyPreview, []);
        Assert.Equal("level 99", File.ReadAllText(save));
    }
    [Fact]
    public async Task SplitPreviewMergesInstalledAndAccountSaveFiles()
    {
        using var work = new TestWorkspace(); var save = work.FileAt("saves/state.sav", "data");
        var (_, engine, service) = work.Services();
        engine.Configure([new() { Title = "Fixture Game", SaveTitle = "Fixture Game", InstallDirectory = work.Folder("Games/Fixture Game") }], [new() { Name = "Fixture Game", Files = [save] }], isolated: true);
        var result = Assert.Single(await service.PreviewAsync()); Assert.Equal("Ready to back up", result.Status); Assert.Single(result.Files);
        Assert.Contains("store: other", File.ReadAllText(Path.Combine(engine.ConfigDirectory, "config.yaml")));
    }
    [Fact]
    public async Task CancellationStopsChildProcessPromptly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(powershell, ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], cancellation.Token));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }
    private sealed class SlowGlobalRunner : IProcessRunner
    {
        private int calls;
        public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default)
        {
            if (++calls > 1) await Task.Delay(Timeout.Infinite, ct);
            return new(0, """{"games":{"Fixture Game":{"change":"New","decision":"Processed","files":{"C:/fixture/save":{"bytes":4,"failed":false}},"registry":{}}}}""", "");
        }
    }
    [Fact]
    public async Task GlobalTimeoutRetainsInstalledCoverageAndRestoresConfiguration()
    {
        using var work = new TestWorkspace(); var (settings, _, _) = work.Services();
        var engine = new Ludusavi(settings, work.Folder("split-config"), new SlowGlobalRunner(), TimeSpan.FromMilliseconds(20));
        engine.Configure([new() { Title = "Fixture Game", SaveTitle = "Fixture Game", InstallDirectory = work.Folder("Games/Fixture Game") }], [], isolated: true);
        using var result = await engine.PreviewAsync(work.Folder("repo"));
        Assert.Single(Ludusavi.ParseStatuses(result, true)); Assert.NotNull(engine.LastScanWarning);
        Assert.Contains("store: other", File.ReadAllText(Path.Combine(engine.ConfigDirectory, "config.yaml")));
    }
    [Fact]
    public async Task ExplicitCancellationDoesNotMasqueradeAsPartialSuccess()
    {
        using var work = new TestWorkspace(); var (settings, _, _) = work.Services();
        var engine = new Ludusavi(settings, work.Folder("split-config"), new SlowGlobalRunner(), TimeSpan.FromSeconds(10));
        engine.Configure([new() { Title = "Fixture Game", SaveTitle = "Fixture Game", InstallDirectory = work.Folder("Games/Fixture Game") }], [], isolated: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.PreviewAsync(work.Folder("repo"), ct: cancellation.Token));
        Assert.Null(engine.LastScanWarning);
        Assert.Contains("store: other", File.ReadAllText(Path.Combine(engine.ConfigDirectory, "config.yaml")));
    }
    [Fact]
    public async Task RunningAndLockedSavesAreDeferred()
    {
        using var work = new TestWorkspace(); var file = work.FileAt("saves/state.sav", "data"); var (_, _, service) = work.Services();
        Assert.Equal("Deferred", Assert.Single(await service.BackUpAsync(["Fixture Game"])).Status);
        using var handle = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // Some engines report an unreadable file at scan time; neither path may publish a backup.
        try { var results = await service.BackUpAsync([]); Assert.DoesNotContain(results, x => x.Status == "Backed up"); } catch (IOException) { }
        Assert.False(File.Exists(Path.Combine(service.Repository, "gsm-integrity.json")));
    }
    [Fact]
    public async Task RetentionPreservesPinnedVersionAndSkipsUnchangedData()
    {
        using var work = new TestWorkspace(); work.FileAt("saves/state.sav", "0"); var (_, engine, service) = work.Services();
        await service.BackUpAsync([]); var first = Assert.Single(await engine.VersionsAsync(service.Repository));
        await service.PinAsync(service.Repository, first, true);
        await service.BackUpAsync([]); Assert.Single(await engine.VersionsAsync(service.Repository));
        for (var i = 1; i <= 5; i++) { await Task.Delay(1050); work.FileAt("saves/state.sav", i.ToString()); await service.BackUpAsync([]); }
        var versions = await engine.VersionsAsync(service.Repository);
        Assert.Equal(4, versions.Count); Assert.Contains(versions, x => x.Id == first.Id && x.Locked);
    }
    [Fact]
    public async Task RestoreRedirectsToNewProfileAndRejectsCorruption()
    {
        using var work = new TestWorkspace(); var old = work.FileAt("old-profile/save.sav", "original"); var (_, engine, service) = work.Services(savePath: Path.GetDirectoryName(old));
        await service.BackUpAsync([]); var version = Assert.Single(await engine.VersionsAsync(service.Repository));
        var destination = work.Folder("new-profile");
        engine.Configure([], [], [new(Path.GetDirectoryName(old)!, destination)], isolated: true);
        var preview = await service.RestorePreviewAsync(service.Repository, version);
        Assert.Contains("new-profile", preview);
        await service.RestoreAsync(service.Repository, version, preview, []);
        Assert.Equal("original", File.ReadAllText(Path.Combine(destination, "save.sav")));
        var archive = Directory.GetFiles(service.Repository, "*.zip", SearchOption.AllDirectories).Single(); File.AppendAllText(archive, "damage");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestorePreviewAsync(service.Repository, version));
    }
    [Fact]
    public async Task RegistryFixtureRoundTripsWithoutTouchingGameKeys()
    {
        using var work = new TestWorkspace(); var key = @"Software\GameSaveManager.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fixture = Registry.CurrentUser.CreateSubKey(key)) fixture.SetValue("Level", 7);
            var (_, engine, service) = work.Services(); engine.Configure([], [new() { Name = "Registry Fixture", Registry = ["HKEY_CURRENT_USER\\" + key] }], isolated: true);
            await service.BackUpAsync([]); var version = Assert.Single(await engine.VersionsAsync(service.Repository));
            using (var fixture = Registry.CurrentUser.CreateSubKey(key)) fixture.SetValue("Level", 99);
            var preview = await service.RestorePreviewAsync(service.Repository, version);
            await service.RestoreAsync(service.Repository, version, preview, []);
            using var restored = Registry.CurrentUser.OpenSubKey(key); Assert.Equal(7, restored!.GetValue("Level"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }
    [Fact]
    public void FileLevelFailuresAreNotReportedAsSuccess()
    {
        using var data = JsonDocument.Parse("""{"games":{"Game":{"change":"Different","decision":"Processed","files":{"C:/save":{"bytes":4,"failed":true,"error":null}},"registry":{}}}}""");
        Assert.Equal("Failed", Assert.Single(Ludusavi.ParseStatuses(data, false)).Status);
    }
    [Fact]
    public void GraphicsConfigurationDoesNotMasqueradeAsProtectedProgress()
    {
        using var data = JsonDocument.Parse("""{"games":{"Game":{"change":"New","decision":"Processed","files":{"C:/Games/Game/hardwaresettings/hardware_settings_config.xml":{"bytes":42,"failed":false}},"registry":{}}}}""");
        Assert.Equal("Settings only; saves unverified", Assert.Single(Ludusavi.ParseStatuses(data, true)).Status);
        Assert.Equal("Settings backed up; saves unverified", Assert.Single(Ludusavi.ParseStatuses(data, false)).Status);
    }
    [Fact]
    public void UnsafeArchiveIsRejected()
    {
        using var work = new TestWorkspace(); var folder = work.Folder("repo");
        using (var zip = ZipFile.Open(Path.Combine(folder, "bad.zip"), ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape").Open()); writer.Write("bad"); }
        Assert.Throws<InvalidDataException>(() => BackupRepository.ValidateArchives(folder));
    }
}
