using System.Text.Json;

namespace GameSaveManager.Core;

public sealed class AppSettings
{
    public List<string> Roots { get; set; } = [@"D:\Games"];
    public string BackupDirectory { get; set; } = Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } oneDrive ? Path.Combine(oneDrive, "Documents", "Game Save Backups") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Game Save Backups");
    public string EnginePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "engine", "ludusavi.exe");
    public string MachineId { get; set; } = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];
    public bool AutomationEnabled { get; set; }
    public bool StartAtSignIn { get; set; } = true;
    public int Retention { get; set; } = 10;
    public DateTimeOffset? LastDailyBackup { get; set; }
    public DateTimeOffset? LastManifestUpdate { get; set; }
    public string PlayniteExecutable { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnite", "Playnite.DesktopApp.exe");
    public string PlayniteDataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite");
    public string PlayniteLibraryDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite", "library");
}

public sealed class GameInstallation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public List<string> ExecutableCandidates { get; set; } = [];
    public string Status { get; set; } = "Needs review";
    public string Reason { get; set; } = "";
    public string? PlayniteId { get; set; }
    public string? SaveTitle { get; set; }
    public bool Confirmed { get; set; }
    public bool Excluded { get; set; }
    public bool Present { get; set; } = true;
    public DateTimeOffset LastChangeUtc { get; set; }
}

public sealed class SaveRule
{
    public string Name { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public List<string> Registry { get; set; } = [];
}

public sealed class SaveStatus
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public long Bytes { get; set; }
    public string Detail { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public List<string> Registry { get; set; } = [];
}

public sealed record BackupVersion(string Game, string Id, string Timestamp, bool Locked)
{
    public override string ToString() => $"{Game} — {Timestamp}{(Locked ? "  [pinned]" : "")}";
}
public sealed record PathRedirect(string Source, string Target);
public sealed record Activity(DateTimeOffset Time, string Message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Empty JSON document.");
}
