using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private bool busy;
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
    private void Refresh()
    {
        var games = app.Store.Get("games", () => new List<GameInstallation>());
        GameCount.Text = games.Count(x => x.Present).ToString();
        var saveStatuses = app.Store.Get("save-statuses", () => new List<SaveStatus>());
        var settingsOnly = saveStatuses.Where(x => x.Status.StartsWith("Settings", StringComparison.Ordinal)).Select(x => x.Name).ToHashSet();
        var attention = games.Where(x => !x.Excluded && (x.Status is "Needs review" or "Missing installation" or "Metadata needs review" || x.SaveTitle == null || settingsOnly.Contains(x.SaveTitle ?? x.Title))).ToList();
        foreach (var game in attention.Where(x => settingsOnly.Contains(x.SaveTitle ?? x.Title))) game.Reason = "Only configuration-like files found; confirm your actual progress save location.";
        AttentionCount.Text = attention.Count.ToString();
        var selectedId = (GamesGrid.SelectedItem as GameInstallation)?.Id;
        GamesGrid.ItemsSource = games; GamesGrid.SelectedItem = games.FirstOrDefault(x => x.Id == selectedId); AttentionGrid.ItemsSource = attention;
        SavesGrid.ItemsSource = app.Store.Get("save-statuses", () => new List<SaveStatus>()); ActivityList.ItemsSource = app.Store.Activities(); StatusText.Text = app.Status;
    }
    public async Task RunAsync(Func<Task> work)
    {
        if (busy) return; busy = true; Tabs.IsEnabled = false;
        try { await work(); }
        catch (OperationCanceledException) { app.Store.Log("Operation cancelled. For a partially completed restore, use the safety location recorded in Recent activity."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Action could not complete", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; Tabs.IsEnabled = true; Refresh(); }
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.ScanAsync());
    private async void FindSaves_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.PreviewSavesAsync());
    private async void Backup_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await app.BackupAsync());
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => app.CancelCurrentOperation();
    private async void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SavesGrid.SelectedItem is SaveStatus save) await RunAsync(async () => await app.BackupAsync([save.Name]));
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
        var result = EditDialog.Show(this, "Confirm game", [("Game title", game.Title), ("Launch executable", game.Executable), ("Launch arguments", game.Arguments)], "Executable candidates:\n" + string.Join("\n", game.ExecutableCandidates));
        if (result != null) await RunAsync(async () => await app.Exclusive(() => { app.ConfirmGame(game.Id, result[0], result[1], result[2]); return Task.FromResult(true); }));
    }
    private async void Exclude_Click(object sender, RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is GameInstallation game) await RunAsync(async () => await app.Exclusive(() => { app.ExcludeGame(game.Id); return Task.FromResult(true); }));
    }
    private async void RetryMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is not GameInstallation game || game.PlayniteId == null) return;
        await RunAsync(async () => await app.Exclusive(() => { app.Store.Set("pending-metadata", new LibraryGame { Id = game.PlayniteId, SourceId = game.Id, Name = game.Title }); return Task.FromResult(true); }));
    }
    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var selected = (SavesGrid.SelectedItem as SaveStatus)?.Name ?? (AttentionGrid.SelectedItem as GameInstallation)?.Title ?? "";
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
    private void OpenBackups_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(app.Settings.BackupDirectory)) Process.Start(new ProcessStartInfo(app.Settings.BackupDirectory) { UseShellExecute = true }); }
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
                RestorePreview.Text = string.Join("\n", statuses.SelectMany(x => new[] { x.Name, "Files to restore:" }.Concat(x.Files).Concat(["Registry keys:"]).Concat(x.Registry)));
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
            try { await app.Backups.RestoreAsync(RepositoryPath.Text, version, previewJson, app.RunningTitles(), app.OperationToken); RestorePreview.Text = "Restore completed. See Recent activity for the safety backup location."; previewJson = null; }
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
        await RunAsync(async () => await app.Exclusive(async () => { await new MigrationService(app).RestorePlayniteAsync(picker.FileName); return true; }));
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
    public static string[]? Show(Window owner, string title, (string Label, string Value)[] fields, string explanation)
    {
        var window = new Window { Owner = owner, Title = title, Width = 760, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(new TextBlock { Text = title, FontSize = 24, Margin = new Thickness(0, 0, 0, 16) });
        var inputs = new List<TextBox>();
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label });
            var input = new TextBox { Text = field.Value, AcceptsReturn = field.Label.Contains("line"), MinHeight = field.Label.Contains("line") ? 75 : 40, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            inputs.Add(input); panel.Children.Add(input);
        }
        panel.Children.Add(new TextBlock { Text = explanation, Foreground = new SolidColorBrush(Color.FromRgb(159, 180, 189)), Margin = new Thickness(0, 8, 0, 20) });
        var buttons = new WrapPanel(); var save = new Button { Content = "Confirm", IsDefault = true }; var cancel = new Button { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) => { window.DialogResult = true; window.Close(); }; buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return window.ShowDialog() == true ? inputs.Select(x => x.Text).ToArray() : null;
    }
}
