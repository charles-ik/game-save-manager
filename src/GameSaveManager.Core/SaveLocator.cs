namespace GameSaveManager.Core;

/// <summary>Finds likely save folders for games the save database does not know, by exact folder-name match.</summary>
public static class SaveLocator
{
    // Executable names shared by many games; matching on these would pair a game with someone else's folder.
    private static readonly HashSet<string> Generic = ["launcher", "launch", "game", "start", "setup", "config", "createdump", "apphost", "binkplay", "steamclientloader", "moduploader", "unitycrashhandler64", "crashreportclient"];

    public static List<string> DefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Paths.DistinctRoots([documents, Path.Combine(home, "Saved Games"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.Combine(home, "AppData", "LocalLow"), Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)]);
    }

    /// <summary>The game's title followed by its distinctive executable names, e.g. "Taxi Life", "TaxiLife".</summary>
    public static List<string> Names(GameInstallation game)
    {
        var executables = game.ExecutableCandidates.Prepend(game.Executable).Where(x => x.Length > 0)
            .Select(x => Path.GetFileNameWithoutExtension(x).Replace("-Win64-Shipping", "", StringComparison.OrdinalIgnoreCase))
            .Where(x => Discovery.TitleKey(x).Length >= 5 && !Generic.Contains(Discovery.TitleKey(x)));
        return executables.Prepend(game.Title).DistinctBy(Discovery.TitleKey).Take(4).ToList();
    }

    /// <summary>Folders one or two levels below the roots whose name equals one of the game's names.</summary>
    public static Dictionary<string, List<string>> Locate(IEnumerable<GameInstallation> games, IEnumerable<string> roots)
    {
        var folders = new List<(string Path, string Key)>();
        foreach (var root in roots)
            foreach (var first in Children(root))
            {
                folders.Add((first, Discovery.TitleKey(Path.GetFileName(first))));
                folders.AddRange(Children(first).Select(x => (x, Discovery.TitleKey(Path.GetFileName(x)))));
            }
        var result = new Dictionary<string, List<string>>();
        foreach (var game in games)
        {
            var keys = Names(game).Select(Discovery.TitleKey).ToHashSet();
            var matches = folders.Where(x => keys.Contains(x.Key) && !Paths.Within(x.Path, game.InstallDirectory)).Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (matches.Count > 0) result[game.Title] = matches;
        }
        return result;
    }
    private static IEnumerable<string> Children(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.GetDirectories(directory) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
