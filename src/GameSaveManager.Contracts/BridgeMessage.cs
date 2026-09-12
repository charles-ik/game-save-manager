namespace GameSaveManager.Contracts;

public sealed class BridgeMessage
{
    public int Version { get; set; } = 1;
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string Error { get; set; } = "";
    public List<LibraryGame> Games { get; set; } = new();
    public LibraryGame? Game { get; set; }
    public string DataDirectory { get; set; } = "";
    public string LibraryDirectory { get; set; } = "";
    public string Executable { get; set; } = "";
}

public sealed class LibraryGame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string SourceId { get; set; } = "";
    public bool IsRunning { get; set; }
}
