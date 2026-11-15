using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameSaveManager.Core;
using GameSaveManager.Contracts;
using Microsoft.Win32;

namespace GameSaveManager.App;

public partial class MainWindow : Window
{
    private readonly AppController app;
    private string? previewJson;
    private string? previewIdentity;
    private bool busy, refreshing;
    private const string AllStatuses = "All statuses";
    public MainWindow(AppController controller)
    {
        InitializeComponent(); app = controller;
        RootsText.Text = string.Join(Environment.NewLine, app.Settings.Roots); DestinationText.Text = app.Settings.BackupDirectory;
        EngineText.Text = app.Settings.EnginePath; PlayniteText.Text = app.Settings.PlayniteExecutable;
        AutomationCheck.IsChecked = app.Settings.AutomationEnabled; StartupCheck.IsChecked = app.Settings.StartAtSignIn;
        RepositoryPath.Text = app.Backups.Repository;
        app.Changed += () => Dispatcher.BeginInvoke(Refresh);
        app.StatusChanged += value => Dispatcher.BeginInvoke(() => StatusText.Text = value);
        Closing += (_, e) => { if (!((App)System.Windows.Application.Current).Exiting) { e.Cancel = true; Hide(); } };
        Refresh();
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (app != null && !refreshing) Refresh(); }
    /// <summary>Shows rows in a grid, keeping the user's column sort and selected rows and applying the search box and status filter.</summary>
    private static void Show<T>(DataGrid grid, List<T> rows, Func<T, string> key, TextBox search, ComboBox? status, Func<T, string> text, Func<T, IEnumerable<string>> states, Func<T, bool>? hidden = null)
    {
        // Background refreshes (Playnite polls every few seconds) must not drop the rows the user has selected.
        var selected = grid.SelectedItems.OfType<T>().Select(key).ToHashSet();
        var sorts = grid.Items.SortDescriptions.ToList(); var chosen = status?.SelectedItem as string ?? AllStatuses;
        if (status != null) { status.ItemsSource = rows.SelectMany(states).Where(x => !x.EndsWith(": ", StringComparison.Ordinal)).Distinct().Order().Prepend(AllStatuses).ToList(); status.SelectedItem = ((List<string>)status.ItemsSource).Contains(chosen) ? chosen : chosen = AllStatuses; }
        grid.ItemsSource = rows;
        foreach (var sort in sorts) grid.Items.SortDescriptions.Add(sort);
        foreach (var column in grid.Columns) column.SortDirection = sorts.Where(x => x.PropertyName == column.SortMemberPath).Select(x => (System.ComponentModel.ListSortDirection?)x.Direction).FirstOrDefault();
        var words = search.Text.Trim();
        // Hidden rows (removed games) appear only when their own status is picked in the filter.
        grid.Items.Filter = row => (chosen == AllStatuses ? hidden?.Invoke((T)row) != true : states((T)row).Contains(chosen)) && text((T)row).Contains(words, StringComparison.OrdinalIgnoreCase);
        foreach (var row in rows.Where(x => selected.Contains(key(x)) && grid.Items.Contains(x))) grid.SelectedItems.Add(row);
    }
    private static List<T> Selected<T>(DataGrid grid) => grid.SelectedItems.OfType<T>().ToList();
    private void Refresh()
    {
        refreshing = true; try { RefreshCore(); } finally { refreshing = false; }
    }
    private void RefreshCore()
    {
        var games = app.Store.Get("games", () => new List<GameInstallation>());
        GameCount.Text = games.Count(x => x.Present && !x.Removed).ToString();
        var saveStatuses = app.Store.Get("save-statuses", () => new List<SaveStatus>());
        var settingsOnly = saveStatuses.Where(x => x.Status.StartsWith("Settings", StringComparison.Ordinal)).Select(x => x.Name).ToHashSet();
        var attention = games.Where(x => !x.Excluded && (x.Status is "Needs review" or "Missing installation" or "Metadata needs review" || x.SaveTitle == null || settingsOnly.Contains(x.SaveTitle ?? x.Title))).ToList();
        foreach (var game in attention.Where(x => settingsOnly.Contains(x.SaveTitle ?? x.Title))) game.Reason = "Only configuration-like files found; confirm your actual progress save location.";
        AttentionCount.Text = attention.Count.ToString();
        var byName = saveStatuses.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.First().Status);
        foreach (var game in games) game.SaveStatus = game.Removed ? "" : game.Excluded ? "Excluded" : !game.Present ? "" : byName.GetValueOrDefault(game.SaveTitle ?? game.Title, "Not scanned yet");
        var missing = games.Count(x => !x.Present && !x.Excluded);
        RemoveMissingButton.Content = $"Remove missing ({missing})…"; RemoveMissingButton.IsEnabled = missing > 0;
        CompanionNotice.Visibility = app.Store.Get("companion-outdated", () => false) ? Visibility.Visible : Visibility.Collapsed;
        ProtectedCount.Text = saveStatuses.Count(x => x.Status is "Backed up" or "Unchanged").ToString();
        Show(GamesGrid, games, x => x.Id, GamesSearch, GamesFilter, x => x.Title + "\n" + x.InstallDirectory, x => ["Playnite: " + x.Status, "Saves: " + x.SaveStatus], x => x.Removed);
        Show(AttentionGrid, attention, x => x.Id, AttentionSearch, null, x => x.Title + "\n" + x.Reason, _ => []);
        Show(SavesGrid, saveStatuses, x => x.Name, SavesSearch, SavesFilter, x => x.Name + "\n" + x.Detail, x => [x.Status]);
        ActivityList.ItemsSource = app.Store.Activities(); StatusText.Text = app.Status;
    }
    public async Task RunAsync(Func<Task> work)
    {
        if (busy) return; busy = true; Tabs.IsHitTestVisible = false; Tabs.Opacity = 0.6; BusyBar.Visibility = CancelButton.Visibility = Visibility.Visible;
        try { await work(); }
        catch (OperationCanceledException) { app.Store.Log("Operation cancelled. For a partially completed restore, use the safety location recorded in Recent activity."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Action could not complete", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; Tabs.IsHitTestVisible = true; Tabs.Opacity = 1; BusyBar.Visibility = CancelButton.Visibility = Visibility.Collapsed; Refresh(); }
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.ScanAsync());
    private async void FindSaves_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.PreviewSavesAsync());
    private async void Backup_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.BackupAsync());
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => app.CancelCurrentOperation();
    private async void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        var names = Selected<SaveStatus>(SavesGrid).Select(x => x.Name).ToList();
        if (names.Count > 0) await RunAsync(async () => await app.BackupAsync(names));
    }
    private void InspectFiles_Click(object sender, RoutedEventArgs e)
    {
        if (SavesGrid.SelectedItem is not SaveStatus save) return;
        EditDialog.Show(this, "Located files: " + save.Name, [], "These are the files and registry keys currently known for this game. Settings may be included; this list does not prove every progress save has been found.\n\n" + string.Join("\n", save.Files.Concat(save.Registry)));
    }
    private async void ReviewGame_Click(object sender, RoutedEventArgs e) => await ReviewGameAsync(GamesGrid.SelectedItem as GameInstallation);
    private async void ReviewAttention_Click(object sender, RoutedEventArgs e) => await ReviewGameAsync(AttentionGrid.SelectedItem as GameInstallation);
    private async Task ReviewGameAsync(GameInstallation? game)
    {
        if (game == null) return;
        var result = EditDialog.Show(this, "Confirm game", [("Game title", game.Title), ("Launch executable", game.Executable), ("Launch arguments", game.Arguments)], "Click a candidate to use it as the launch executable. It is also copied to the clipboard.", ("Executable candidates", game.ExecutableCandidates, 1));
        if (result != null) await RunAsync(async () => await app.Exclusive(() => { app.ConfirmGame(game.Id, result[0], result[1], result[2]); return Task.FromResult(true); }));
    }
    private async void Exclude_Click(object sender, RoutedEventArgs e)
    {
        var games = Selected<GameInstallation>(GamesGrid);
        if (games.Count > 0) await RunAsync(async () => await app.Exclusive(() => { foreach (var game in games) app.ExcludeGame(game.Id); return Task.FromResult(true); }));
    }
    private async void RemoveMissing_Click(object sender, RoutedEventArgs e)
    {
        var missing = app.Games.Where(x => !x.Present && !x.Excluded).Select(x => x.Title).ToList();
        if (missing.Count == 0 || MessageBox.Show(this, "Remove these games from the list? Their installation folder was not found in the last scan. Existing backups and Playnite entries are not touched.\n\n" + string.Join("\n", missing), "Remove missing games", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async () => await app.Exclusive(() => Task.FromResult(app.RemoveMissingGames())));
    }
    private async void LocateSaves_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var found = await Task.Run(app.LocateUnknownSaves);
        if (found.Count == 0) { MessageBox.Show(this, "No folders matching your unknown games were found in Documents, Saved Games or AppData. Use Add save location for those games.", "Locate unknown saves"); return; }
        var summary = string.Join("\n", found.Select(x => x.Key + "\n" + string.Join("\n", x.Value.Select(p => "    " + p))));
        if (EditDialog.Show(this, $"Save folders found for {found.Count} games", [("Folders to back up (remove any that are wrong)", summary)], "These folders have the same name as the game or its program. Confirm to add them as save locations and back them up now. They may include settings as well as progress.") is not { } result) return;
        // Read the list back so removed lines are respected: a title line followed by its indented folders.
        var rules = new List<SaveRule>();
        foreach (var line in result[0].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (!char.IsWhiteSpace(line[0])) rules.Add(new() { Name = line.Trim() }); else if (rules.Count > 0) rules[^1].Files.Add(line.Trim());
        rules.RemoveAll(x => x.Files.Count == 0 || !found.ContainsKey(x.Name));
        if (rules.Count == 0) return;
        await app.Exclusive(() => { foreach (var rule in rules) app.SaveRule(rule); return Task.FromResult(true); });
        await app.BackupAsync(rules.Select(x => x.Name));
    });
    private async void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        var games = Selected<GameInstallation>(GamesGrid); if (games.Count == 0) return;
        if (MessageBox.Show(this, (games.Count == 1 ? "Remove \"" + games[0].Title + "\" from Game Save Manager?" : $"Remove these {games.Count} games from Game Save Manager?\n\n" + string.Join("\n", games.Select(x => x.Title))) + "\n\nIt will no longer be listed, scanned, backed up or sent to Playnite. The games, their saves and existing backups are not deleted.\n\nTo bring one back later, pick \"Playnite: Removed\" in the status filter and use Review game.", "Remove game", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async () => await app.Exclusive(() => { foreach (var game in games) app.RemoveGame(game.Id); return Task.FromResult(true); }));
    }
    private async void RetryMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is not GameInstallation game) return;
        if (game.PlayniteId == null) { MessageBox.Show(this, "This game is not in Playnite yet. Confirm it with Review game, then open Playnite so it can be imported.", "Retry metadata"); return; }
        await RunAsync(async () => await app.Exclusive(() => { app.Store.Set("pending-metadata", new LibraryGame { Id = game.PlayniteId, SourceId = game.Id, Name = game.Title }); return Task.FromResult(true); }));
    }
    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var selected = (Tabs.SelectedIndex == 2 ? (AttentionGrid.SelectedItem as GameInstallation)?.Title : (SavesGrid.SelectedItem as SaveStatus)?.Name) ?? "";
        var old = app.Rules.FirstOrDefault(x => x.Name == selected);
        var suggestions = SuggestLocations(selected);
        var result = EditDialog.Show(this, "Confirm save location", [("Game name", selected), ("Save files or folders — one per line", string.Join("\n", old?.Files ?? [])), ("Registry keys — optional, one per line", string.Join("\n", old?.Registry ?? []))], "Confirm only locations that contain this game's saves.\nPossible folders (not verified):\n" + string.Join("\n", suggestions));
        if (result != null) await RunAsync(async () => await app.Exclusive(() => { app.SaveRule(new() { Name = result[0].Trim(), Files = Lines(result[1]), Registry = Lines(result[2]) }); return Task.FromResult(true); }));
    }
    private static List<string> SuggestLocations(string title)
    {
        if (title.Length < 3) return [];
        var key = Discovery.TitleKey(title); var result = new List<string>();
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow") };
        foreach (var root in roots.Where(Directory.Exists))
            try { result.AddRange(Directory.EnumerateDirectories(root).Where(x => Discovery.TitleKey(Path.GetFileName(x)).Contains(key, StringComparison.OrdinalIgnoreCase))); } catch (UnauthorizedAccessException) { }
        return result;
    }
    private static List<string> Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private static string? PickFolder(string title, string? initial = null)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initial != null && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    private void AddRoot_Click(object sender, RoutedEventArgs e) { var folder = PickFolder("Choose a game collection folder"); if (folder != null) RootsText.Text += Environment.NewLine + folder; }
    private void ChooseDestination_Click(object sender, RoutedEventArgs e) { var folder = PickFolder("Choose the backup destination", DestinationText.Text); if (folder != null) DestinationText.Text = folder; }
    private void ChooseEngine_Click(object sender, RoutedEventArgs e) { var picker = new OpenFileDialog { Filter = "Ludusavi|ludusavi.exe" }; if (picker.ShowDialog(this) == true) EngineText.Text = picker.FileName; }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.Exclusive(() =>
    {
        var destination = Path.GetFullPath(DestinationText.Text);
        var roots = Paths.DistinctRoots(Lines(RootsText.Text));
        if (roots.Any(x => Paths.Within(destination, x) || Paths.Within(x, destination))) throw new InvalidOperationException("Backup and game folders must not overlap.");
        if (!File.Exists(EngineText.Text)) throw new FileNotFoundException("Select the Ludusavi executable.");
        Directory.CreateDirectory(destination);
        app.Settings.Roots = roots; app.Settings.BackupDirectory = destination; app.Settings.EnginePath = EngineText.Text;
        app.Settings.PlayniteExecutable = PlayniteText.Text; app.Settings.AutomationEnabled = AutomationCheck.IsChecked == true;
        app.Settings.StartAtSignIn = StartupCheck.IsChecked == true; app.SaveSettings();
        ((App)System.Windows.Application.Current).ApplyStartupSetting(); RepositoryPath.Text = app.Backups.Repository;
        app.Store.Log("Settings saved."); return Task.FromResult(true);
    }));
    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(app.Settings.BackupDirectory)) Process.Start(new ProcessStartInfo(app.Settings.BackupDirectory) { UseShellExecute = true });
        else MessageBox.Show(this, "The backup folder does not exist yet:\n" + app.Settings.BackupDirectory + "\n\nCheck it in Settings and choose Save settings.", "Open backup folder");
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "JSON coverage report|*.json", FileName = "game-save-coverage.json" };
        if (picker.ShowDialog(this) == true) await RunAsync(() => { File.WriteAllText(picker.FileName, Json.Write(new { generatedAt = DateTimeOffset.UtcNow, games = app.Games, saves = app.Saves })); return Task.CompletedTask; });
    }
    private void BrowseRepository_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("Choose a source PC's Saves or Safety repository", app.Settings.BackupDirectory);
        if (path == null) return; RepositoryPath.Text = path;
        var machineFile = Path.Combine(Path.GetDirectoryName(path)!, "machine.json");
        if (File.Exists(machineFile))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(machineFile)); var source = document.RootElement;
            MappingsText.Text = source.GetProperty("documents").GetString() + " | " + Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\n" + source.GetProperty("userProfile").GetString() + " | " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
    private async void LoadVersions_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.Exclusive(async () =>
    {
        var source = RepositoryPath.Text; BackupRepository.Verify(source); VersionsList.ItemsSource = await app.Engine.VersionsAsync(source); return true;
    }));
    private List<PathRedirect> ReadMappings()
    {
        var mappings = new List<PathRedirect>();
        foreach (var line in Lines(MappingsText.Text))
        {
            var pair = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !Path.IsPathFullyQualified(pair[0]) || !Path.IsPathFullyQualified(pair[1])) throw new ArgumentException("Each mapping needs two absolute paths separated by |.");
            mappings.Add(new(Paths.Canonical(pair[0]), Paths.Canonical(pair[1])));
        }
        if (mappings.GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) throw new ArgumentException("Each source folder can have only one destination.");
        return mappings.OrderByDescending(x => x.Source.Length).ToList();
    }
    private string PreviewIdentity(BackupVersion version) => RepositoryPath.Text + "\n" + version.Game + "\n" + version.Id + "\n" + MappingsText.Text;
    private async void PreviewRestore_Click(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not BackupVersion version) return;
        await RunAsync(async () => await app.Exclusive(async () =>
        {
            app.ConfigureEngine(ReadMappings());
            try
            {
                previewJson = await app.Backups.RestorePreviewAsync(RepositoryPath.Text, version, app.OperationToken); previewIdentity = PreviewIdentity(version);
                using var document = JsonDocument.Parse(previewJson);
                var statuses = Ludusavi.ParseStatuses(document, true);
                RestorePreview.Text = string.Join("\n", statuses.SelectMany(x => new[] { x.Name, "Files to restore:" }.Concat(x.Files).Concat(["Registry keys:"]).Concat(x.Registry))); RestoreButton.IsEnabled = true;
            }
            finally { app.ConfigureEngine(); }
            return true;
        }));
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not BackupVersion version || previewJson == null || previewIdentity != PreviewIdentity(version)) { MessageBox.Show(this, "Preview this version and its destinations first."); return; }
        if (MessageBox.Show(this, "Restore the selected version to the displayed destinations? Existing data will be backed up before replacement.\n\n" + version, "Confirm restore", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async () => await app.Exclusive(async () =>
        {
            app.ConfigureEngine(ReadMappings());
            try { await app.Backups.RestoreAsync(RepositoryPath.Text, version, previewJson, app.RunningTitles(), app.OperationToken); RestorePreview.Text = "Restore completed. See Recent activity for the safety backup location."; previewJson = null; RestoreButton.IsEnabled = false; }
            finally { app.ConfigureEngine(); }
            return true;
        }));
    }
    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not BackupVersion version) return;
        await RunAsync(async () => await app.Exclusive(async () => { await app.Backups.PinAsync(RepositoryPath.Text, version, !version.Locked); VersionsList.ItemsSource = await app.Engine.VersionsAsync(RepositoryPath.Text); return true; }));
    }
    private async void PrepareMigration_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Create pinned save backups and a complete Playnite package? Playnite may restart. Close games first. Unknown save locations will be listed in the package's coverage report.", "Prepare migration", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await RunAsync(async () => { var path = await new MigrationService(app).PrepareAsync(); MessageBox.Show(this, "Migration package created locally:\n" + path + "\n\nCheck OneDrive has finished uploading before moving PCs."); });
    }
    private async void RestorePlaynite_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Playnite package|PlayniteBackup-*.zip" };
        if (picker.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "Replace the current Playnite library and settings with this package? A safety package will be created first. Playnite will restart.", "Restore Playnite", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async () => await app.Exclusive(async () => { await new MigrationService(app).RestorePlayniteAsync(picker.FileName, app.OperationToken); return true; }));
    }
    private async void ImportRules_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "App migration configuration|app-configuration.json" };
        if (picker.ShowDialog(this) != true) return;
        await RunAsync(async () => await app.Exclusive(() =>
        {
            using var data = JsonDocument.Parse(File.ReadAllText(picker.FileName)); var mappings = ReadMappings();
            var rules = Json.Read<List<SaveRule>>(data.RootElement.GetProperty("rules").GetRawText());
            foreach (var rule in rules) rule.Files = rule.Files.Select(path => Remap(path, mappings)).ToList();
            if (MessageBox.Show(this, "Import these custom save rules using the displayed path mappings?\n\n" + string.Join("\n", rules.Select(x => x.Name + ": " + string.Join(", ", x.Files))), "Import custom rules", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return Task.FromResult(false);
            foreach (var rule in rules) app.SaveRule(rule);
            return Task.FromResult(true);
        }));
    }
    private static string Remap(string path, List<PathRedirect> mappings)
    {
        var match = mappings.FirstOrDefault(x => Paths.Within(path, x.Source));
        return match == null ? path : Path.Combine(match.Target, Path.GetRelativePath(match.Source, path));
    }
    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.Exclusive(() =>
    {
        var mappings = ReadMappings(); var snapshot = app.Store.Get("playnite-snapshot", () => new List<LibraryGame>());
        var changes = snapshot.Where(x => x.InstallDirectory.Length > 0 && mappings.Any(m => Paths.Within(x.InstallDirectory, m.Source))).ToList();
        foreach (var game in changes) { game.Executable = game.Executable.Length > 0 ? Remap(game.Executable, mappings) : ""; game.InstallDirectory = Remap(game.InstallDirectory, mappings); }
        if (changes.Any(x => x.Executable.Length > 0 && !File.Exists(x.Executable))) throw new InvalidOperationException("Some mapped launch files do not exist. Correct your mappings or install the games first.");
        if (MessageBox.Show(this, "Update these Playnite installation and launch paths?\n\n" + string.Join("\n", changes.Select(x => x.Name + " → " + x.InstallDirectory)), "Reconnect games", MessageBoxButton.YesNo) == MessageBoxResult.Yes) app.Store.Set("pending-remap", changes);
        return Task.FromResult(true);
    }));
    private void InstallCompanion_Click(object sender, RoutedEventArgs e)
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "GameSaveManager.Playnite.pext");
        if (!File.Exists(packaged)) { MessageBox.Show(this, "Build the release package with scripts/package.ps1 first. The companion .pext file is included beside the app."); return; }
        Process.Start(new ProcessStartInfo(packaged) { UseShellExecute = true });
    }
    private async void ConfigurePlayniteBackups_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.Exclusive(() => { new MigrationService(app).ConfigureRoutineBackups(); MessageBox.Show(this, "Daily startup backups are configured. Playnite will retain three complete packages in your backup folder."); return Task.FromResult(true); }));
    public void SaveScreenshot(string file)
    {
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        UpdateLayout(); var content = (FrameworkElement)Content; var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(file); encoder.Save(output);
    }
    public void SaveTabScreenshots(string file)
    {
        for (var i = 0; i < Tabs.Items.Count; i++)
        {
            Tabs.SelectedIndex = i;
            SaveScreenshot(i == 0 ? file : Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "-" + i + ".png"));
        }
        Tabs.SelectedIndex = 0;
    }
}

internal static class EditDialog
{
    public static string[]? Show(Window owner, string title, (string Label, string Value)[] fields, string explanation, (string Label, List<string> Items, int Field)? picks = null)
    {
        var window = new Window { Owner = owner, Title = title, Width = 760, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Background = owner.Background; window.Foreground = owner.Foreground; window.FontSize = owner.FontSize;
        var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        var inputs = new List<TextBox>();
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            var input = new TextBox { Text = field.Value, AcceptsReturn = field.Label.Contains("line"), MinHeight = field.Label.Contains("line") ? 75 : 40, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 16) };
            inputs.Add(input); panel.Children.Add(input);
        }
        if (picks is { Items.Count: > 0 } pick)
        {
            // Clicking an item fills the given field and copies it, so paths never need retyping.
            panel.Children.Add(new TextBlock { Text = pick.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            var list = new ListBox { ItemsSource = pick.Items, MaxHeight = 180, SelectedItem = pick.Items.FirstOrDefault(x => x == inputs[pick.Field].Text) };
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is not string value) return;
                inputs[pick.Field].Text = value;
                try { Clipboard.SetText(value); } catch (System.Runtime.InteropServices.ExternalException) { } // clipboard held by another program
            };
            panel.Children.Add(list);
        }
        panel.Children.Add(new TextBlock { Text = explanation, Foreground = new SolidColorBrush(Color.FromRgb(159, 180, 189)), Margin = new Thickness(0, 8, 0, 20) });
        var buttons = new WrapPanel(); var save = new Button { Content = fields.Length == 0 ? "Close" : "Confirm", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Style = (Style)owner.FindResource("Primary") }; var cancel = new Button { Content = "Cancel", IsCancel = true, Visibility = fields.Length == 0 ? Visibility.Collapsed : Visibility.Visible };
        save.Click += (_, _) => { window.DialogResult = true; window.Close(); }; buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return window.ShowDialog() == true ? inputs.Select(x => x.Text).ToArray() : null;
    }
}

internal sealed class StatusBrush : IValueConverter
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x8E)), Warn = new SolidColorBrush(Color.FromRgb(0xE3, 0xBC, 0x7A)), Bad = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)), Idle = new SolidColorBrush(Color.FromRgb(0x6B, 0x78, 0x7E));
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "In Playnite" or "Backed up" or "Unchanged" => Good,
        "Failed" or "Missing installation" => Bad,
        "Excluded" or "Removed" or "No saves found" or "Unknown location" => Idle,
        _ => Warn // review, waiting, queued, deferred, ready, settings-only
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
