using System.IO;
using System.Threading;
using System.Windows;
using GameSaveManager.Core;
using Microsoft.Win32;

namespace GameSaveManager.App;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private System.Windows.Forms.NotifyIcon? tray;
    public AppController Controller { get; private set; } = null!;
    public bool Exiting { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke-test");
        instance = new Mutex(true, "Local\\" + PlayniteBridge.PipeName, out var created);
        if (!created) { Shutdown(); return; }
        var stateArgument = Array.IndexOf(e.Args, "--state-dir");
        var state = stateArgument >= 0 && stateArgument + 1 < e.Args.Length ? e.Args[stateArgument + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSaveManager");
        Controller = new(state);
        if (e.Args.Contains("--coverage-only"))
        {
            var outputIndex = Array.IndexOf(e.Args, "--output");
            if (outputIndex < 0 || outputIndex + 1 >= e.Args.Length) { Shutdown(2); return; }
            Controller.Settings.BackupDirectory = Path.Combine(state, "preview-target");
            try
            {
                await Controller.ScanAsync(); await Controller.PreviewSavesAsync();
                Paths.AtomicWrite(e.Args[outputIndex + 1], Json.Write(new { generatedAt = DateTimeOffset.UtcNow, games = Controller.Games, saves = Controller.Saves, activity = Controller.Store.Activities() }));
                Shutdown(0);
            }
            catch (Exception ex) { Paths.AtomicWrite(e.Args[outputIndex + 1], Json.Write(new { error = ex.ToString() })); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--scan-only"))
        {
            var outputIndex = Array.IndexOf(e.Args, "--output");
            if (outputIndex < 0 || outputIndex + 1 >= e.Args.Length) { Shutdown(2); return; }
            var warnings = new List<string>(); var games = new Discovery().Scan(Controller.Settings.Roots, warnings.Add);
            Controller.Store.Set("games", games);
            Paths.AtomicWrite(e.Args[outputIndex + 1], Json.Write(new { generatedAt = DateTimeOffset.UtcNow, games, warnings }));
            Shutdown(0); return;
        }
        DispatcherUnhandledException += (_, args) => { Controller.Store.Log(args.Exception.ToString()); MessageBox.Show(args.Exception.Message, "Game Save Manager"); args.Handled = true; };
        var window = new MainWindow(Controller); MainWindow = window;
        if (smoke)
        {
            window.Show(); window.UpdateLayout();
            var outputIndex = Array.IndexOf(e.Args, "--screenshot");
            if (outputIndex >= 0 && outputIndex + 1 < e.Args.Length) window.SaveTabScreenshots(e.Args[outputIndex + 1]);
            Exiting = true; window.Close(); Shutdown(0); return;
        }
        tray = new() { Icon = System.Drawing.SystemIcons.Application, Text = "Game Save Manager", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Game Save Manager", null, (_, _) => Dispatcher.Invoke(() => { window.Show(); window.Activate(); }));
        menu.Items.Add("Back up now", null, (_, _) => Dispatcher.Invoke(async () => await window.RunAsync(async () => { await Controller.BackupAsync(); })));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() => { Exiting = true; Shutdown(); }));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { window.Show(); window.Activate(); });
        Controller.NeedsAttention += message => Dispatcher.Invoke(() => tray.ShowBalloonTip(5000, "Game Save Manager needs attention", message, System.Windows.Forms.ToolTipIcon.Warning));
        Controller.Start();
        if (!e.Args.Contains("--tray")) window.Show();
    }
    public void ApplyStartupSetting()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (Controller.Settings.StartAtSignIn) key.SetValue("GameSaveManager", "\"" + Environment.ProcessPath + "\" --tray");
        else key.DeleteValue("GameSaveManager", false);
    }
    protected override void OnExit(ExitEventArgs e) { tray?.Dispose(); Controller?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
