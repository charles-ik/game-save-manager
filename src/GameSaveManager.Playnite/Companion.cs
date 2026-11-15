using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using GameSaveManager.Contracts;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GameSaveManager.Playnite;

public sealed class Companion : GenericPlugin
{
    private readonly System.Timers.Timer timer;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim pollGate = new(1, 1);
    private readonly HashSet<string> metadataInProgress = new();
    private readonly ILogger log = LogManager.GetLogger();
    private readonly HashSet<string> acknowledged;
    private string LedgerPath => Path.Combine(GetPluginUserDataPath(), "imports.json");
    public override Guid Id => Guid.Parse("03f6c998-5c32-4e62-9e76-5eaa9c1cf687");

    public Companion(IPlayniteAPI api) : base(api)
    {
        Properties = new GenericPluginProperties { HasSettings = false };
        acknowledged = File.Exists(LedgerPath) ? JsonConvert.DeserializeObject<HashSet<string>>(File.ReadAllText(LedgerPath)) ?? new() : new();
        timer = new System.Timers.Timer(15000);
        timer.Elapsed += async (_, _) => await PollAsync();
    }
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args) { timer.Start(); _ = PollAsync(); }
    public override void OnApplicationStopped(OnApplicationStoppedEventArgs args) { timer.Stop(); }
    public override void OnGameStarted(OnGameStartedEventArgs args) => _ = SendEventAsync("gameStarted", args.Game);
    public override void OnGameStopped(OnGameStoppedEventArgs args) => _ = SendEventAsync("gameStopped", args.Game);
    private async Task SendEventAsync(string kind, Game game)
    {
        try { await ExchangeAsync(new BridgeMessage { Kind = kind, Game = ConvertGame(game) }); }
        catch (Exception ex) { log.Debug("Game Save Manager is unavailable: " + ex.Message); }
    }
    private LibraryGame ConvertGame(Game game)
    {
        var action = game.GameActions?.FirstOrDefault(x => x.IsPlayAction && x.Type == GameActionType.File);
        return new LibraryGame
        {
            Id = game.Id.ToString(), Name = game.Name ?? "", InstallDirectory = game.InstallDirectory ?? "",
            Executable = action == null ? "" : PlayniteApi.ExpandGameVariables(game, action.Path ?? ""),
            Arguments = action?.Arguments ?? "", IsRunning = game.IsRunning
        };
    }
    private async Task PollAsync()
    {
        if (!await pollGate.WaitAsync(0)) return;
        try
        {
            var snapshot = PlayniteApi.MainView.UIDispatcher.Invoke(() => PlayniteApi.Database.Games.Select(ConvertGame).ToList());
            var response = await ExchangeAsync(new BridgeMessage
            {
                Kind = "poll", Companion = 2, Games = snapshot, DataDirectory = PlayniteApi.Paths.ConfigurationPath,
                LibraryDirectory = PlayniteApi.Database.DatabasePath,
                Executable = Path.Combine(PlayniteApi.Paths.ApplicationPath, "Playnite.DesktopApp.exe")
            });
            if (response?.Kind == "import" && response.Game != null)
            {
                try
                {
                    LibraryGame imported = null!;
                    await PlayniteApi.MainView.UIDispatcher.InvokeAsync(() => imported = Import(response.RequestId, response.Game));
                    await ExchangeAsync(new BridgeMessage { Kind = "imported", Game = imported });
                    _ = EnrichAsync(imported);
                }
                catch (Exception ex) { await ExchangeAsync(new BridgeMessage { Kind = "importFailed", Game = response.Game, Error = ex.Message }); }
            }
            else if (response?.Kind == "metadata" && response.Game != null) _ = EnrichAsync(response.Game);
            else if (response?.Kind == "remap")
            {
                await PlayniteApi.MainView.UIDispatcher.InvokeAsync(() => Remap(response.Games));
                await ExchangeAsync(new BridgeMessage { Kind = "remapped" });
            }
        }
        catch (Exception ex) { log.Debug("Game Save Manager bridge: " + ex.Message); }
        finally { pollGate.Release(); }
    }
    internal LibraryGame Import(string requestId, LibraryGame incoming)
    {
        var existing = PlayniteApi.Database.Games.FirstOrDefault(x => string.Equals(x.InstallDirectory?.TrimEnd('\\', '/'), incoming.InstallDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || x.GameId == "gsm:" + incoming.SourceId);
        if (existing == null)
        {
            if (acknowledged.Contains(incoming.SourceId)) throw new InvalidOperationException("This import was previously removed from Playnite; review it in Game Save Manager.");
            if (!File.Exists(incoming.Executable)) throw new FileNotFoundException("Game executable is unavailable.");
            existing = new Game
            {
                Name = incoming.Name, GameId = "gsm:" + incoming.SourceId, InstallDirectory = incoming.InstallDirectory, IsInstalled = true,
                GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>
                {
                    new() { Name = "Play", Type = GameActionType.File, Path = incoming.Executable, Arguments = incoming.Arguments, WorkingDir = incoming.InstallDirectory, IsPlayAction = true }
                }
            };
            PlayniteApi.Database.Games.Add(existing);
        }
        acknowledged.Add(incoming.SourceId);
        Directory.CreateDirectory(GetPluginUserDataPath());
        File.WriteAllText(LedgerPath, JsonConvert.SerializeObject(acknowledged));
        var result = ConvertGame(existing); result.SourceId = incoming.SourceId; return result;
    }
    private async Task EnrichAsync(LibraryGame incoming)
    {
        lock (metadataInProgress) { if (!metadataInProgress.Add(incoming.Id)) return; }
        string error = "";
        try
        {
            var game = PlayniteApi.MainView.UIDispatcher.Invoke(() => PlayniteApi.Database.Games.Get(Guid.Parse(incoming.Id)));
            if (game == null) throw new InvalidOperationException("Game no longer exists in Playnite.");
            await Task.Run(async () =>
            {
                var provider = PlayniteApi.Addons.Plugins.OfType<MetadataPlugin>().FirstOrDefault(x => x.Name.IndexOf("IGDB", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? throw new InvalidOperationException("Enable Playnite's IGDB metadata provider and retry metadata.");
                var metadata = provider.GetMetadataProvider(new MetadataRequestOptions(game, true));
                var args = new GetMetadataFieldArgs();
                var name = metadata.AvailableFields.Contains(MetadataField.Name) ? metadata.GetName(args) : null;
                string Key(string? value) => System.Text.RegularExpressions.Regex.Replace(value ?? "", @"[^\p{L}\p{N}]", "").ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(name) || Key(name) != Key(game.Name)) throw new InvalidOperationException("Metadata match is uncertain. Review the title or select metadata directly in Playnite, then retry.");
                var description = metadata.AvailableFields.Contains(MetadataField.Description) ? metadata.GetDescription(args) : null;
                var cover = metadata.AvailableFields.Contains(MetadataField.CoverImage) ? metadata.GetCoverImage(args) : null;
                var background = metadata.AvailableFields.Contains(MetadataField.BackgroundImage) ? metadata.GetBackgroundImage(args) : null;
                // Download assets in the background before dispatching database updates.
                var coverPath = cover == null ? null : PrepareImage(cover);
                var backgroundPath = background == null ? null : PrepareImage(background);
                try
                {
                    await PlayniteApi.MainView.UIDispatcher.InvokeAsync(() =>
                    {
                        var current = PlayniteApi.Database.Games.Get(game.Id);
                        if (current == null) return;
                        if (string.IsNullOrWhiteSpace(current.Description) && description != null) current.Description = description;
                        if (string.IsNullOrWhiteSpace(current.CoverImage) && coverPath != null) current.CoverImage = PlayniteApi.Database.AddFile(coverPath, current.Id);
                        if (string.IsNullOrWhiteSpace(current.BackgroundImage) && backgroundPath != null) current.BackgroundImage = PlayniteApi.Database.AddFile(backgroundPath, current.Id);
                        PlayniteApi.Database.Games.Update(current);
                    });
                }
                finally { if (coverPath != null) File.Delete(coverPath); if (backgroundPath != null) File.Delete(backgroundPath); }
            });
        }
        catch (Exception ex) { error = ex.Message; log.Warn("Metadata needs review: " + error); }
        finally { lock (metadataInProgress) metadataInProgress.Remove(incoming.Id); }
        try { await ExchangeAsync(new BridgeMessage { Kind = "metadataUpdated", Game = incoming, Error = error }); }
        catch (Exception ex) { log.Debug("Could not report metadata result: " + ex.Message); }
    }
    internal void Remap(List<LibraryGame> updates)
    {
        foreach (var update in updates)
        {
            if (!Guid.TryParse(update.Id, out var id) || PlayniteApi.Database.Games.Get(id) == null || !Directory.Exists(update.InstallDirectory) || (update.Executable.Length > 0 && !File.Exists(update.Executable))) throw new InvalidOperationException("A mapped game or destination is unavailable. Review mappings again.");
        }
        foreach (var update in updates)
        {
            var game = PlayniteApi.Database.Games.Get(Guid.Parse(update.Id));
            var action = game.GameActions?.FirstOrDefault(x => x.IsPlayAction && x.Type == GameActionType.File);
            if (action != null && update.Executable.Length > 0)
            {
                action.Path = update.Executable;
                if (string.Equals(action.WorkingDir, game.InstallDirectory, StringComparison.OrdinalIgnoreCase)) action.WorkingDir = update.InstallDirectory;
            }
            game.InstallDirectory = update.InstallDirectory; game.IsInstalled = true; PlayniteApi.Database.Games.Update(game);
        }
    }
    private string PrepareImage(MetadataFile image)
    {
        var extension = Path.GetExtension(image.FileName ?? image.Path ?? "image.png");
        if (extension.Length == 0 || extension.Length > 8) extension = ".png";
        var path = Path.Combine(GetPluginUserDataPath(), Guid.NewGuid().ToString("N") + extension);
        Directory.CreateDirectory(GetPluginUserDataPath());
        if (image.Content != null) File.WriteAllBytes(path, image.Content);
        else if (Uri.TryCreate(image.Path, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
        { using var client = new System.Net.WebClient(); client.DownloadFile(uri, path); }
        else File.Copy(image.Path, path);
        return path;
    }
    private string AddImage(MetadataFile image, Guid gameId)
    {
        if (image.Content == null) return PlayniteApi.Database.AddFile(image.Path, gameId);
        var temporary = Path.Combine(GetPluginUserDataPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(image.FileName));
        Directory.CreateDirectory(GetPluginUserDataPath());
        try { File.WriteAllBytes(temporary, image.Content); return PlayniteApi.Database.AddFile(temporary, gameId); }
        finally { File.Delete(temporary); }
    }
    private async Task<BridgeMessage?> ExchangeAsync(BridgeMessage message)
    {
        if (!await connectionGate.WaitAsync(12000)) return null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", "GameSaveManager-" + WindowsIdentity.GetCurrent().User.Value, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(2000, timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonConvert.SerializeObject(message));
            var read = reader.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(10000)) != read) return null;
            var line = await read;
            return line == null ? null : JsonConvert.DeserializeObject<BridgeMessage>(line);
        }
        finally { connectionGate.Release(); }
    }
}
